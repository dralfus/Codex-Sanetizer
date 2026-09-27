using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

// Ticket 314: the first pointer Send is decided from resident, precomputed
// target evidence WITHOUT live UI Automation inside the low-level callback.
// Most tests drive the production pointer classification
// (TrayProtectionController.ClassifySendControl) through the explicit resident
// test seam with a caller-owned ProtectionSnapshot and store; the last test
// drives the actual protected pointer operation through the registered native
// hook callback. Deterministic: no timers, no real UIA, no cloud, no raw prompts.
[TestFixture]
public class PointerTargetEvidenceTests : SanitizerTests
{
    private const string SelectedProfileId = "codex-desktop";
    private const uint SelectedProcessId = 0x1234;
    private static readonly IntPtr SelectedWindow = new(0x2A1F3C);
    private static readonly IntPtr UnrelatedWindow = new(0xB00BEE);

    // Records every live-UIA call so tests can assert the resident decision
    // path never consults it.
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

        public SendControlDiscoveryResult Discover(NativePointerGesture gesture) => _result;

        public SendControlDiscoveryResult DiscoverFocusedControl(IntPtr capturedTargetWindow) => _result;
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

    // The resident seam classifies against the controller's own store, so tests
    // publish their evidence into it before driving a classification.
    private static TrayProtectionController CreateController()
    {
        var controller = TrayProtectionController.CreateTest(
            new NoOpTrayHotkeyHost(),
            () => new OsInteractionResult(
                OsInteractionStatusIds.NotConfigured,
                null,
                null,
                null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()));
        // The live-discovery path publishes into the store; tests that want a
        // pre-populated store publish explicitly after creation.
        controller.PointerTargetEvidenceForTesting().Clear();
        return controller;
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

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(result.Diagnostics["pointer_target_identity"], Is.EqualTo("resident_evidence"));
        Assert.That(uia.CallCount, Is.Zero,
            "Resident pointer decision must not invoke live UIA inside the low-level callback.");
    }

    [Test]
    public void SlowOrUnavailableUiaCannotPassSelectedClientSendOrConsumeUnrelatedClick()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();

        // An unrelated click with fresh Unrelated evidence passes through from
        // resident state — never suppressed, never consuming live UIA.
        store.Publish(Identity(7, UnrelatedWindow), 0x9999, PointerTargetVerdict.Unrelated, generation: 7);
        var unrelated = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow, 0x9999));
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
        Assert.That(unrelated.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(uia.CallCount, Is.Zero);

        // Selected client with NO resident evidence: the callback must not
        // suppress on profile-name equality alone; the gesture keeps the
        // live-discovery classification (here: uncertain suppression), never a
        // silent pass-through of a selected-client Send.
        var selected = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));
        Assert.That(selected.SuppressOriginalInput, Is.True);
        Assert.That(selected.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));

        // The uncertain live verdict is published for the gesture's exact
        // (window, process) pair, so the next callback decides by bounded lookup
        // only.
        Assert.That(store.TryResolve(SelectedWindow, out var entry), Is.True);
        Assert.That(entry.TargetProcessId, Is.EqualTo(SelectedProcessId));
        Assert.That(entry.Verdict, Is.EqualTo(PointerTargetVerdict.SelectedSend));
        // Ticket 314 AC3: the fail-closed outcome stays investigable — it carries
        // the resident target identity and generations, not a bare status.
        var stale = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 8),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));
        Assert.That(stale.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(stale.Diagnostics["profile_id"], Is.EqualTo(SelectedProfileId));
        Assert.That(stale.Diagnostics["pointer_target_identity"], Is.EqualTo("resident_evidence"));
        Assert.That(stale.Diagnostics["snapshot_generation"], Is.EqualTo("8"));

        Assert.That(uia.CallCount, Is.EqualTo(1),
            "Only the first gesture may consult live UIA; resident evidence decides the rest.");
    }

    [Test]
    public void FreshUnrelatedEvidencePassesThroughWithoutLiveUia()
    {
        var uia = new CountingSendControlDiscovery();
        var controller = CreateController();
        var store = controller.PointerTargetEvidenceForTesting();

        // A published Unrelated verdict resolves as pass-through from resident
        // evidence, consuming no click and calling no live UIA.
        store.Publish(Identity(7, UnrelatedWindow), 0x9999, PointerTargetVerdict.Unrelated, generation: 7);
        var unrelated = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow, 0x9999));

        Assert.That(unrelated.SuppressOriginalInput, Is.False);
        Assert.That(unrelated.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(uia.CallCount, Is.Zero);
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

        // Runtime replacement clears the resident store. The next gesture is
        // decided by the live path again — never by the removed entry.
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
    public void MissingLiveAdapterSuppressesEveryGestureFailClosed()
    {
        var controller = CreateController();

        // UIA unavailable (no adapter) and no evidence: the callback cannot tell
        // a Send target from navigation, so nothing is allowed through — both a
        // selected-client gesture and an unattributed one are suppressed.
        var selected = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, discovery: null, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));
        Assert.That(selected.SuppressOriginalInput, Is.True);
        Assert.That(selected.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(selected.Diagnostics["pointer_target_identity"], Is.EqualTo("resident_evidence"));

        var unattributed = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, discovery: null, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow, 0x9999));
        Assert.That(unattributed.SuppressOriginalInput, Is.True);
        Assert.That(unattributed.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
    }

    [Test]
    public void ResidentEvidenceExecutesTheActualProtectedPointerOperationOnce()
    {
        // Drives the real low-level callback path: the hook host's pointer
        // classifier is the controller's production ClassifySendControl and the
        // suppressed gesture invokes RunNativeSendControlOnce, so a decision made
        // entirely from resident evidence drives the actual protected pointer
        // operation exactly once.
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedPointerDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.SelectedClientUncertain,
                TestSurfaceFactory.CreateNativeSubmitDiscovery("codex-desktop"))),
            profile,
            () => submitCalls++);

        Assert.That(tray.Start(), Is.True);

        // The first gesture is decided by live discovery: the uncertain verdict
        // suppresses the click fail-closed without executing the submit, and is
        // published into the resident store for the gesture's own
        // (window, process) pair.
        hook.TriggerPointer(new NativePointerGesture(10, 10, "left", SelectedWindow, SelectedProcessId));
        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(hook.LastPointerClassification?.SuppressOriginalInput, Is.True);
        Assert.That(submitCalls, Is.Zero);
        var store = tray.PointerTargetEvidenceForTesting();
        Assert.That(store.TryResolve(SelectedWindow, out var entry), Is.True);
        Assert.That(entry.TargetProcessId, Is.EqualTo(SelectedProcessId));
        Assert.That(entry.Verdict, Is.EqualTo(PointerTargetVerdict.SelectedSend));

        // The next gesture in the same window is decided from resident evidence
        // before the callback returns — no second live UIA call — and its
        // verdict drives the actual protected pointer operation
        // (RunNativeSendControlOnce -> protected send pipeline).
        hook.TriggerPointer(new NativePointerGesture(30, 30, "left", SelectedWindow, SelectedProcessId));

        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(hook.LastPointerClassification?.Diagnostics["pointer_target_identity"],
            Is.EqualTo("resident_evidence"));
        Assert.That(submitCalls, Is.EqualTo(1),
            "Resident evidence must drive the protected pointer operation, not only classification.");
        Assert.That(tray.State.ProtectedSendAttemptStatus, Is.EqualTo("sent_safely"),
            "The resident verdict must drive the protected send pipeline to its terminal outcome.");
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Does.Contain("send_detected"));
    }
}
