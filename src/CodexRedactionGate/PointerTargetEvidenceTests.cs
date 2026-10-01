using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;
using static SanitizerTests;

// Ticket 314: the first pointer Send is decided from resident, precomputed
// target evidence WITHOUT live UI Automation inside the low-level callback.
// Resident admission performs bounded lookup only;
// the reference-composer path (ClassifyPointerSendViaDiscoveryForTesting)
// exercises live UIA outside the callback and publishes owner-verified evidence.
// Deterministic: no timers, no real UIA, no cloud; only synthetic fixture text.
[TestFixture]
public class PointerTargetEvidenceTests
{
    private const string SelectedProfileId = "codex-desktop";
    private const uint SelectedProcessId = 0x1234;
    private static readonly IntPtr SelectedWindow = new(0x2A1F3C);
    private static readonly IntPtr UnrelatedWindow = new(0xB00BEE);

    private static NativePointerGesture Gesture(IntPtr window, uint process) =>
        new(10, 10, "left", window, process);

    // Records every live-UIA call so tests can assert the callback path never
    // consults it.
    private sealed class CountingSendControlDiscovery : ISendControlDiscovery
    {
        public int CallCount { get; private set; }

        public SendControlDiscoveryResult Discover(NativePointerGesture gesture)
        {
            CallCount++;
            return new SendControlDiscoveryResult(
                SendControlClassification.SelectedClientUncertain,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.SurfaceUnverified));
        }

        public SendControlDiscoveryResult DiscoverFocusedControl(IntPtr capturedTargetWindow)
        {
            CallCount++;
            return new SendControlDiscoveryResult(
                SendControlClassification.SelectedClientUncertain,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.SurfaceUnverified));
        }
    }

    private sealed class NoOpTrayHotkeyHost : ITrayHotkeyHost
    {
        public HotkeyBinding Binding { get; } = new("fake-hotkey", "Ctrl+Enter", "test");

        public string? LastErrorCode => null;

        public bool Start(Action onTriggered) => true;

        public void Stop()
        {
        }
    }

    // Local pointer-tray fixture: production hook registration whose pointer
    // classifier is the controller's production ClassifySendControl.
    private sealed class FixedPointerDiscovery : ISendControlDiscovery
    {
        private readonly SendControlDiscoveryResult _result;

        public FixedPointerDiscovery(SendControlDiscoveryResult result) => _result = result;

        public int CallCount { get; private set; }

        public SendControlDiscoveryResult Discover(NativePointerGesture gesture)
        {
            CallCount++;
            return _result;
        }

        public SendControlDiscoveryResult DiscoverFocusedControl(IntPtr capturedTargetWindow)
        {
            CallCount++;
            return _result;
        }
    }

    private static TrayProtectionController CreatePointerTray(
        FakeNativeSubmitHookHost hook,
        ISendControlDiscovery sendControlDiscovery,
        SubmitBindingProfile profile,
        Action onSubmit,
        Func<NativeSubmitTargetIdentity, Func<string, string, bool>, Func<bool>, Func<IDisposable?>, OsInteractionResult>? targetRunner = null)
    {
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                profileSnapshot: NativeSubmitProfileSnapshot.FromProfile(profile)),
            () =>
            {
                onSubmit();
                return CreateSubmittedResult(profile.ProfileId);
            },
            profile,
            ResidentTargetTracedRunner: targetRunner);
        return TrayProtectionController.CreateTest(
            new NoOpTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            sendControlDiscovery: sendControlDiscovery,
            activeSurfaceDiscovery: () => TestSurfaceFactory.CreateNativeSubmitDiscovery(profile.ProfileId),
            selectedWindowProfileResolver: _ => profile.ProfileId,
            nativeSubmitRuntimes: new[] { runtime });
    }

    private static TextSurfaceDescriptor CreateSurfaceWithWindowHandle(string profileId, string windowHandle)
    {
        var surface = TestSurfaceFactory.CreateNativeSubmitSurface(profileId);
        return surface with
        {
            Metadata = surface.Metadata with { WindowHandle = windowHandle }
        };
    }

    private static TrayProtectionController CreateController()
    {
        return TrayProtectionController.CreateTest(
            new NoOpTrayHotkeyHost(),
            () => new OsInteractionResult(
                OsInteractionStatusIds.NotConfigured,
                null,
                null,
                null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()));
    }

    private static ProtectionSnapshot MakeSnapshot(
        TrayProtectionController controller,
        ISendControlDiscovery? discovery,
        long generation) =>
        controller.ReadSnapshotForTesting() with
        {
            Generation = generation,
            SendControlDiscovery = discovery,
            State = controller.ReadSnapshotForTesting().State with
            {
                LastProfileId = SelectedProfileId,
                ConfiguredProfileId = SelectedProfileId
            }
        };

    private static NativeSubmitTargetIdentity Identity(long snapshotGeneration, IntPtr window) =>
        new(
            SnapshotGeneration: snapshotGeneration,
            ProfileId: SelectedProfileId,
            WindowHandle: window.ToInt64().ToString("X"));

    [Test]
    public void SendEvidenceDoesNotConsumeAnotherControlInTheSameWindow()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submits = 0;
        var discovery = new FixedPointerDiscovery(new SendControlDiscoveryResult(
            SendControlClassification.IdentifiedSend,
            ChatGptDiscoveryFixture.CreateVerified(CreateSurfaceWithWindowHandle(SelectedProfileId, "2A1F3C"))));
        var tray = CreatePointerTray(hook, discovery, profile, () => submits++);
        Assert.That(tray.Start(), Is.True);
        tray.ClassifyPointerSendViaDiscoveryForTesting(tray.ReadSnapshotForTesting(),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        hook.TriggerPointer(new NativePointerGesture(30, 30, "left", SelectedWindow, SelectedProcessId));

        Assert.That(hook.LastPointerClassification!.SuppressOriginalInput, Is.False);
        Assert.That(submits, Is.Zero);
        Assert.That(discovery.CallCount, Is.EqualTo(1));
        tray.Stop();
    }

    [Test]
    public void MissingResidentEvidenceNeverInvokesDiscovery()
    {
        var controller = CreateController();
        var discovery = new CountingSendControlDiscovery();
        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, discovery, 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(discovery.CallCount, Is.Zero);
    }

    [TestCase(null)]
    [TestCase("other-client")]
    public void StaleSendEvidenceFailsClosedRegardlessOfLastTrayProfile(string? lastProfile)
    {
        var controller = CreateController();
        var snapshot = MakeSnapshot(controller, null, 7);
        snapshot = snapshot with { State = snapshot.State with { LastProfileId = lastProfile } };
        controller.PointerTargetEvidenceForTesting().Publish(
            Identity(6, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, 6);

        var result = controller.ClassifyPointerSendForTesting(snapshot,
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
    }

    [Test]
    public void StaleNonSendEvidencePassesThroughInSelectedWindow()
    {
        var controller = CreateController();
        controller.PointerTargetEvidenceForTesting().Publish(
            Identity(6, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.Unrelated, 6);
        var result = controller.ClassifyPointerSendForTesting(MakeSnapshot(controller, null, 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void SendAndNonSendEvidenceCoexistInOneWindow()
    {
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        var send = Gesture(SelectedWindow, SelectedProcessId);
        var navigation = send with { X = 30, Y = 30 };
        store.Publish(Identity(7, SelectedWindow), send, PointerTargetVerdict.SelectedSend, 7);
        store.Publish(Identity(7, SelectedWindow), navigation, PointerTargetVerdict.Unrelated, 7);
        var snapshot = MakeSnapshot(controller, new CountingSendControlDiscovery(), 7);

        Assert.That(controller.ClassifyPointerSendForTesting(snapshot, navigation).SuppressOriginalInput, Is.False);
        Assert.That(controller.ClassifyPointerSendForTesting(snapshot, send).SuppressOriginalInput, Is.True);
    }

    [TestCase("right", false)]
    [TestCase("LEFT", true)]
    public void EvidenceMatchesOnlyTheVerifiedButton(string button, bool suppressed)
    {
        var controller = CreateController();
        var send = Gesture(SelectedWindow, SelectedProcessId);
        controller.PointerTargetEvidenceForTesting().Publish(Identity(7, SelectedWindow), send, PointerTargetVerdict.SelectedSend, 7);

        Assert.That(controller.ClassifyPointerSendForTesting(MakeSnapshot(controller, null, 7),
            send with { Button = button }).SuppressOriginalInput, Is.EqualTo(suppressed));
    }

    [Test]
    public void ChildWindowEvidenceRetainsNormalizedComposerAnchor()
    {
        var controller = CreateController();
        var child = new IntPtr(0xF00);
        var target = NativeSubmitTargetIdentity.TryCreateForGesture(7,
            CreateSurfaceWithWindowHandle(SelectedProfileId, "2A1F3C"), child,
            _ => SelectedWindow)!;
        controller.PointerTargetEvidenceForTesting().Publish(target, Gesture(child, SelectedProcessId),
            PointerTargetVerdict.SelectedSend, 7);

        var decision = ResidentPointerTargetDecision.TryDecide(controller.PointerTargetEvidenceForTesting(),
            Gesture(child, SelectedProcessId), MakeSnapshot(controller, null, 7));

        Assert.That(decision.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(decision.Target, Is.SameAs(target));
        Assert.That(decision.Target.WindowHandle, Is.EqualTo("2A1F3C"));
    }

    [Test]
    public void InvalidationKeepsRegisteredCallbackInBoundedMode()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var discovery = new CountingSendControlDiscovery();
        var submits = 0;
        var tray = CreatePointerTray(hook, discovery, profile, () => submits++);
        Assert.That(tray.Start(), Is.True);
        var snapshot = tray.ReadSnapshotForTesting();
        var store = tray.PointerTargetEvidenceForTesting();
        store.Publish(Identity(snapshot.Generation, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId),
            PointerTargetVerdict.SelectedSend, snapshot.Generation);
        store.Clear();

        hook.TriggerPointer(Gesture(SelectedWindow, SelectedProcessId));

        Assert.That(hook.LastPointerClassification!.SuppressOriginalInput, Is.False);
        Assert.That(discovery.CallCount, Is.Zero);
        Assert.That(submits, Is.Zero);
        tray.Stop();
    }

    [Test]
    public void FirstPointerSendDecidedFromResidentEvidenceBeforeCallbackReturns()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        store.Publish(Identity(7, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 7);

        // The very first callback gesture is decided from precomputed evidence:
        // guarded, with zero live UIA calls.
        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(result.Diagnostics["pointer_target_identity"], Is.EqualTo("resident_evidence"));
        Assert.That(uia.CallCount, Is.Zero,
            "The callback must never invoke live UIA inside classification.");
    }

    [Test]
    public void SlowOrUnavailableUiaCannotPassSelectedClientSendOrConsumeUnrelatedClick()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();

        // Selected client with fresh Send evidence: suppressed from resident
        // state even though UIA is slow/unavailable — never passed through.
        store.Publish(Identity(7, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 7);
        var selected = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));
        Assert.That(selected.SuppressOriginalInput, Is.True);
        Assert.That(selected.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));

        // Unrelated click with fresh Unrelated evidence: pass-through, not consumed.
        store.Publish(Identity(7, UnrelatedWindow), Gesture(UnrelatedWindow, 0x9999), PointerTargetVerdict.Unrelated, generation: 7);
        var unrelated = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow, 0x9999));
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
        Assert.That(unrelated.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));

        Assert.That(uia.CallCount, Is.Zero,
            "Neither fail-closed suppression nor unrelated pass-through may call live UIA.");
    }

    [Test]
    public void UnidentifiedClickWithoutEvidenceAlwaysPassesThrough()
    {
        var controller = CreateController();

        // No entry at all: the callback cannot identify the control, so the
        // click is never classified or suppressed — SPEC keeps ordinary clicks,
        // copy/paste, navigation, and non-Send controls at normal behavior.
        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, discovery: null, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow, 0x9999));

        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
    }

    [Test]
    public void StaleEvidenceSuppressesSelectedClientAsTraceUnavailable()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        // Evidence published at snapshot generation 6 is stale for snapshot 7.
        store.Publish(Identity(6, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 6);

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(uia.CallCount, Is.Zero);
    }

    [Test]
    public void StaleEvidenceForUnrelatedWindowPassesThrough()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        // A stale Unrelated entry published for a different (window, process)
        // pair resolves as pass-through; the callback consumes nothing it did
        // not verify.
        store.Publish(
            new NativeSubmitTargetIdentity(6, "other-client", UnrelatedWindow.ToInt64().ToString("X")),
            Gesture(UnrelatedWindow, 0x9999),
            PointerTargetVerdict.Unrelated,
            generation: 6);

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow, 0x9999));

        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
    }

    [Test]
    public void CrossProcessGestureDoesNotUseAnotherOwnersEvidence()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        // Evidence was published for one exact (window, process) pair; a gesture
        // whose window belongs to a different process is not the same target.
        store.Publish(Identity(7, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 7);

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, 0x4321));

        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(uia.CallCount, Is.Zero);
    }

    [Test]
    public void ClearedResidentEvidenceNeverFallsBackToLiveDiscovery()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        store.Publish(Identity(7, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 7);

        // Clearing evidence does not authorize discovery inside admission.
        store.Clear();
        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(uia.CallCount, Is.Zero);
    }

    [Test]
    public void FocusChangeInvalidatesWindowEvidenceByHandle()
    {
        var store = new PointerTargetEvidenceStore();
        store.Publish(Identity(7, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 7);

        store.Invalidate(SelectedWindow);

        Assert.That(store.TryResolve(Gesture(SelectedWindow, SelectedProcessId), out _), Is.False);
    }

    [Test]
    public void CanonicalHandleFormattingPreservesVerifiedPointIdentity()
    {
        var store = new PointerTargetEvidenceStore();
        // Publication carries the non-padded handle string as production
        // identities do; resolution arrives as the captured IntPtr.
        store.Publish(Identity(7, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 7);

        Assert.That(store.TryResolve(Gesture(SelectedWindow, SelectedProcessId), out var byIntPtr), Is.True);
        Assert.That(PointerTargetEvidenceKey.FromHandle("0x00000000002A1F3C"), Is.EqualTo("2A1F3C"));
        Assert.That(byIntPtr.Target.WindowHandle, Is.EqualTo("2A1F3C"));
    }

    [Test]
    public void EvidenceGenerationAndNormalizedIdentityReachResidentPointerOperation()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        store.Publish(Identity(7, SelectedWindow), Gesture(SelectedWindow, SelectedProcessId), PointerTargetVerdict.SelectedSend, generation: 7);

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.Diagnostics["evidence_generation"], Is.EqualTo("7"));
        Assert.That(result.Diagnostics["profile_id"], Is.EqualTo(SelectedProfileId));
        Assert.That(result.Diagnostics["snapshot_generation"], Is.EqualTo("7"));
        Assert.That(uia.CallCount, Is.Zero);
    }

    [Test]
    public void UnverifiedDiscoveryPublishesNoEvidence()
    {
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();

        // Reference-composer discovery says "uncertain" (SurfaceUnverified):
        // no owner-verified identity exists, so nothing may become Send evidence.
        var result = controller.ClassifyPointerSendViaDiscoveryForTesting(
            MakeSnapshot(controller, new CountingSendControlDiscovery(), generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.True, "Uncertain still suppresses this gesture.");
        Assert.That(store.TryResolve(Gesture(SelectedWindow, SelectedProcessId), out _), Is.False,
            "SurfaceUnverified must never be upgraded to resident Send evidence.");
    }

    [Test]
    public void VerifiedNonSendControlPublishesUnrelatedEvidence()
    {
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();

        // Reference-composer discovery verifies a NON-Send control: the verdict
        // is published as Unrelated, so the next gesture passes through.
        var result = controller.ClassifyPointerSendViaDiscoveryForTesting(
            MakeSnapshot(controller, new FixedPointerDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.NonSendControl,
                ChatGptDiscoveryFixture.CreateVerified(
                    CreateSurfaceWithWindowHandle(SelectedProfileId, "2A1F3C")))), generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(store.TryResolve(Gesture(SelectedWindow, SelectedProcessId), out var entry), Is.True);
        Assert.That(entry.Verdict, Is.EqualTo(PointerTargetVerdict.Unrelated));
    }

    [Test]
    public void VerifiedSendEvidenceFromReferenceComposerDrivesFirstCallbackSend()
    {
        // The reference composer verifies the Send control out-of-band (live UIA
        // outside the callback); the FIRST production callback gesture in that
        // window is then decided from resident evidence with zero live UIA.
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var composerWindow = new IntPtr(1);
        var surface = new ProductFlowTextSurface("Connect to 192.168.10.25");
        NativeSubmitTargetIdentity? operationTarget = null;
        var uia = new FixedPointerDiscovery(new SendControlDiscoveryResult(
            SendControlClassification.IdentifiedSend,
            ChatGptDiscoveryFixture.CreateVerified(
                CreateSurfaceWithWindowHandle("codex-desktop", "1"))));
        var tray = CreatePointerTray(hook, uia, profile,
            () => throw new InvalidOperationException("The captured-target runner is required."),
            (target, trace, guard, lease) =>
            {
                operationTarget = target;
                return CreateProductFlowOrchestrator(surface, ConfirmationDecisionContract.Confirm,
                    new CapturedTargetSurfaceDiscovery(surface, target)).RunOnce(
                        OsInteractionRunOptions.ConfirmAndSend, trace, guard, lease);
            });

        Assert.That(tray.Start(), Is.True);
        var snapshot = tray.ReadSnapshotForTesting();

        // Out-of-band verification publishes owner-verified Send evidence.
        tray.ClassifyPointerSendViaDiscoveryForTesting(
            snapshot,
            Gesture(composerWindow, SelectedProcessId));
        var uiaCallsAfterPublish = uia.CallCount;
        Assert.That(tray.PointerTargetEvidenceForTesting().TryResolve(Gesture(composerWindow, SelectedProcessId), out var entry), Is.True);
        Assert.That(entry.Verdict, Is.EqualTo(PointerTargetVerdict.SelectedSend));
        Assert.That(entry.TargetProcessId, Is.EqualTo(SelectedProcessId));

        // The first callback gesture: guarded from evidence, no new UIA call.
        hook.TriggerPointer(Gesture(composerWindow, SelectedProcessId));

        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(hook.LastPointerClassification?.Diagnostics["pointer_target_identity"],
            Is.EqualTo("resident_evidence"));
        Assert.That(operationTarget, Is.EqualTo(entry.Target));
        Assert.That(operationTarget!.SnapshotGeneration, Is.EqualTo(snapshot.Generation));
        Assert.That(surface.WriteCount, Is.EqualTo(1));
        Assert.That(surface.CurrentText, Does.Not.Contain("192.168.10.25"));
        Assert.That(surface.SubmitCount, Is.EqualTo(1),
            "Resident evidence must drive the protected pointer operation exactly once.");
        Assert.That(uia.CallCount, Is.EqualTo(uiaCallsAfterPublish),
            "The callback gesture must not consult live UIA.");
        Assert.That(tray.State.ProtectedSendAttemptStatus, Is.EqualTo("sent_safely"));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Count(trace => trace.Stage == "sent_safely"), Is.EqualTo(1));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.All(trace => trace.SnapshotGeneration == snapshot.Generation), Is.True);
        Assert.That(ProtectedSendTrace.IsCompleteSafeSendTrace(tray.State.ProtectedSendAttemptTrace!), Is.True);
        tray.Stop();
    }
}
