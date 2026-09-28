using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

// Ticket 314: the first pointer Send is decided from resident, precomputed
// target evidence WITHOUT live UI Automation inside the low-level callback.
// The production callback (ClassifySendControl) performs bounded lookup only;
// the reference-composer path (ClassifyPointerSendViaDiscoveryForTesting)
// exercises live UIA outside the callback and publishes owner-verified evidence.
// Deterministic: no timers, no real UIA, no cloud, no raw prompts.
[TestFixture]
public class PointerTargetEvidenceTests : SanitizerTests
{
    private const string SelectedProfileId = "codex-desktop";
    private const uint SelectedProcessId = 0x1234;
    private static readonly IntPtr SelectedWindow = new(0x2A1F3C);
    private static readonly IntPtr UnrelatedWindow = new(0xB00BEE);

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
        Action onSubmit)
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
            profile);
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
    public void FirstPointerSendDecidedFromResidentEvidenceBeforeCallbackReturns()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        store.Publish(Identity(7, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 7);

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
        store.Publish(Identity(7, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 7);
        var selected = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));
        Assert.That(selected.SuppressOriginalInput, Is.True);
        Assert.That(selected.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));

        // Unrelated click with fresh Unrelated evidence: pass-through, not consumed.
        store.Publish(Identity(7, UnrelatedWindow), 0x9999, PointerTargetVerdict.Unrelated, generation: 7);
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
        store.Publish(Identity(6, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 6);

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
        // A fresh Unrelated entry published for a different (window, process)
        // pair resolves as pass-through; the callback consumes nothing it did
        // not verify.
        store.Publish(
            new NativeSubmitTargetIdentity(7, "other-client", UnrelatedWindow.ToInt64().ToString("X")),
            0x9999,
            PointerTargetVerdict.Unrelated,
            generation: 7);

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow, 0x9999));

        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
    }

    [Test]
    public void CrossProcessGestureOnSelectedWindowFailsClosed()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        // Evidence was published for one exact (window, process) pair; a gesture
        // whose window belongs to a different process is not the same target.
        store.Publish(Identity(7, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 7);

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, 0x4321));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(uia.CallCount, Is.Zero);
    }

    [Test]
    public void RuntimeReplacementInvalidatesResidentEvidence()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        store.Publish(Identity(7, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 7);

        // Runtime replacement clears the resident store. The next gesture has no
        // evidence, so it is decided by the live path again — never by the
        // removed verdict.
        store.Clear();
        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified),
            "A cleared store falls back to live classification, not the removed verdict.");
        Assert.That(uia.CallCount, Is.EqualTo(1));
    }

    [Test]
    public void FocusChangeInvalidatesWindowEvidenceByHandle()
    {
        var store = new PointerTargetEvidenceStore();
        store.Publish(Identity(7, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 7);

        store.Invalidate(SelectedWindow);

        Assert.That(store.TryResolve(SelectedWindow, out _), Is.False);
    }

    [Test]
    public void ChildToRootTransitionResolvesByCanonicalHandleKey()
    {
        var store = new PointerTargetEvidenceStore();
        // Publication carries the non-padded handle string as production
        // identities do; resolution arrives as the captured IntPtr.
        store.Publish(Identity(7, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 7);

        Assert.That(store.TryResolve(SelectedWindow, out var byIntPtr), Is.True);
        Assert.That(store.TryResolve("2A1F3C", out var byString), Is.True);
        Assert.That(store.TryResolve("00000000002A1F3C", out var byPadded), Is.True);
        Assert.That(byString.EvidenceGeneration, Is.EqualTo(byIntPtr.EvidenceGeneration));
        Assert.That(byPadded.EvidenceGeneration, Is.EqualTo(byIntPtr.EvidenceGeneration));
    }

    [Test]
    public void EvidenceGenerationAndNormalizedIdentityReachResidentPointerOperation()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();
        store.Publish(Identity(7, SelectedWindow), SelectedProcessId, PointerTargetVerdict.SelectedSend, generation: 7);

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
        Assert.That(store.TryResolve(SelectedWindow, out _), Is.False,
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
        Assert.That(store.TryResolve(SelectedWindow, out var entry), Is.True);
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
        var submitCalls = 0;
        var uia = new FixedPointerDiscovery(new SendControlDiscoveryResult(
            SendControlClassification.IdentifiedSend,
            ChatGptDiscoveryFixture.CreateVerified(
                CreateSurfaceWithWindowHandle("codex-desktop", "2A1F3C"))));
        var tray = CreatePointerTray(hook, uia, profile, () => submitCalls++);

        Assert.That(tray.Start(), Is.True);
        var snapshot = tray.ReadSnapshotForTesting();

        // Out-of-band verification publishes owner-verified Send evidence.
        tray.ClassifyPointerSendViaDiscoveryForTesting(
            snapshot,
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));
        var uiaCallsAfterPublish = uia.CallCount;
        Assert.That(tray.PointerTargetEvidenceForTesting().TryResolve(SelectedWindow, out var entry), Is.True);
        Assert.That(entry.Verdict, Is.EqualTo(PointerTargetVerdict.SelectedSend));
        Assert.That(entry.TargetProcessId, Is.EqualTo(SelectedProcessId));

        // The first callback gesture: guarded from evidence, no new UIA call.
        hook.TriggerPointer(new NativePointerGesture(30, 30, "left", SelectedWindow, SelectedProcessId));

        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(hook.LastPointerClassification?.Diagnostics["pointer_target_identity"],
            Is.EqualTo("resident_evidence"));
        Assert.That(submitCalls, Is.EqualTo(1),
            "Resident evidence must drive the protected pointer operation exactly once.");
        Assert.That(uia.CallCount, Is.EqualTo(uiaCallsAfterPublish),
            "The callback gesture must not consult live UIA.");
    }
}
