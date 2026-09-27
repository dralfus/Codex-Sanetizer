using System;
using System.Collections.Generic;
using NUnit.Framework;
using CodexRedactionGate;

// Ticket 314: the first pointer Send is decided from resident, precomputed
// target evidence WITHOUT live UI Automation inside the low-level callback.
// These tests drive the production pointer classification
// (TrayProtectionController.ClassifySendControl) through the explicit test
// seam with a caller-owned ProtectionSnapshot. Deterministic: no timers, no
// real UIA, no cloud, no raw prompts.
[TestFixture]
public class PointerTargetEvidenceTests
{
    private const string SelectedProfileId = "codex-desktop";
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

    private static TrayProtectionController CreateController() =>
        TrayProtectionController.CreateTest(
            new NoOpTrayHotkeyHost(),
            () => new OsInteractionResult(
                OsInteractionStatusIds.NotConfigured,
                null,
                null,
                null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()));

    private static ProtectionSnapshot MakeSnapshot(
        TrayProtectionController controller,
        PointerTargetEvidenceStore store,
        ISendControlDiscovery? discovery,
        long generation) =>
        controller.ReadSnapshotForTesting() with
        {
            Generation = generation,
            SendControlDiscovery = discovery,
            PointerTargetEvidence = store,
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
        var store = new PointerTargetEvidenceStore();
        store.Publish(Identity(7, SelectedWindow), PointerTargetVerdict.SelectedSend, generation: 7);
        var controller = CreateController();

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, store, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow));

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
        var store = new PointerTargetEvidenceStore();
        var controller = CreateController();

        // Selected client with NO resident evidence: fail closed trace_unavailable,
        // never pass-through, even though the UIA adapter is available.
        var selected = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, store, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow));
        Assert.That(selected.SuppressOriginalInput, Is.True);
        Assert.That(selected.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));

        // Unrelated click with fresh Unrelated evidence: pass-through, not consumed.
        store.Publish(Identity(7, UnrelatedWindow), PointerTargetVerdict.Unrelated, generation: 7);
        var unrelated = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, store, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", UnrelatedWindow));
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
        Assert.That(unrelated.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));

        Assert.That(uia.CallCount, Is.Zero,
            "Neither fail-closed suppression nor unrelated pass-through may call live UIA.");
    }

    [Test]
    public void StaleEvidenceSuppressesSelectedClientAsTraceUnavailable()
    {
        var uia = new CountingSendControlDiscovery();
        var store = new PointerTargetEvidenceStore();
        // Evidence published at snapshot generation 6 is stale for snapshot 7.
        store.Publish(Identity(6, SelectedWindow), PointerTargetVerdict.SelectedSend, generation: 6);
        var controller = CreateController();

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, store, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(uia.CallCount, Is.Zero);
    }

    [Test]
    public void RuntimeReplacementInvalidatesResidentEvidence()
    {
        var uia = new CountingSendControlDiscovery();
        var store = new PointerTargetEvidenceStore();
        store.Publish(Identity(7, SelectedWindow), PointerTargetVerdict.SelectedSend, generation: 7);
        var controller = CreateController();

        // Runtime replacement clears the resident store: the next selected-client
        // Send must fail closed instead of trusting the removed entry.
        store.Clear();
        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, store, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow));

        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
    }

    [Test]
    public void FocusChangeInvalidatesWindowEvidenceByHandle()
    {
        var store = new PointerTargetEvidenceStore();
        store.Publish(Identity(7, SelectedWindow), PointerTargetVerdict.SelectedSend, generation: 7);

        store.Invalidate(SelectedWindow);

        Assert.That(store.TryResolve(SelectedWindow, out _), Is.False);
    }

    [Test]
    public void ChildToRootTransitionResolvesByCanonicalHandleKey()
    {
        var store = new PointerTargetEvidenceStore();
        // Publication carries the non-padded handle string as production
        // identities do; resolution arrives as the captured IntPtr.
        store.Publish(Identity(7, SelectedWindow), PointerTargetVerdict.SelectedSend, generation: 7);

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
        var store = new PointerTargetEvidenceStore();
        store.Publish(Identity(7, SelectedWindow), PointerTargetVerdict.SelectedSend, generation: 7);
        var controller = CreateController();

        var result = controller.ClassifyPointerSendForTesting(
            MakeSnapshot(controller, store, uia, generation: 7),
            new NativePointerGesture(10, 10, "left", SelectedWindow));

        Assert.That(result.Diagnostics["evidence_generation"], Is.EqualTo("7"));
        Assert.That(result.Diagnostics["profile_id"], Is.EqualTo(SelectedProfileId));
        Assert.That(result.Diagnostics["snapshot_generation"], Is.EqualTo("7"));
        Assert.That(uia.CallCount, Is.Zero);
    }
}
