using System;
using System.Collections.Generic;
using NUnit.Framework;
using CodexRedactionGate;

// Ticket 314: the first pointer Send must be decided from resident, precomputed
// target evidence WITHOUT live UIA inside the low-level callback. These tests are
// RED by design: they compile against the future resident-evidence seam
// (PointerTargetEvidenceStore / ResidentPointerTargetDecision) that does not
// exist in production yet. No timers, no real UIA, no cloud, no raw prompts.
[TestFixture]
public class PointerTargetEvidenceTests
{
    private const string SelectedProfileId = "codex-desktop";
    private const string SelectedWindowHandle = "00000000002A1F3C";
    private const string UnrelatedWindowHandle = "0000000000B00BEE";

    // Fake UIA adapter: records every live-UIA call so the tests can assert the
    // resident decision path never touches it.
    private sealed class FakeSendControlDiscovery : ISendControlDiscovery
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

    [Test]
    public void FirstPointerSendDecidedFromResidentEvidenceBeforeCallbackReturns()
    {
        var uia = new FakeSendControlDiscovery();
        var store = new PointerTargetEvidenceStore();
        var identity = new NativeSubmitTargetIdentity(
            SnapshotGeneration: 1,
            ProfileId: SelectedProfileId,
            WindowHandle: SelectedWindowHandle);
        store.Publish(identity, PointerTargetVerdict.SelectedSend, generation: 7);

        var decision = ResidentPointerTargetDecision.TryDecide(
            store,
            uia,
            new NativePointerGesture(10, 10, "left", ParseWindow(SelectedWindowHandle)));

        Assert.That(decision, Is.Not.Null);
        Assert.That(decision!.Verdict, Is.EqualTo(PointerTargetVerdict.SelectedSend));
        Assert.That(uia.CallCount, Is.Zero,
            "Resident pointer decision must not invoke live UIA inside the low-level callback.");
    }

    [Test]
    public void SlowOrUnavailableUiaCannotPassSelectedClientSendOrConsumeUnrelatedClick()
    {
        var uia = new FakeSendControlDiscovery();
        var store = new PointerTargetEvidenceStore();
        // Selected client has NO fresh resident evidence and the UIA adapter is
        // unavailable: fail closed with trace_unavailable, never pass-through.
        var selected = ResidentPointerTargetDecision.TryDecide(
            store,
            uia,
            new NativePointerGesture(10, 10, "left", ParseWindow(SelectedWindowHandle)),
            selectedProfileIdResolver: _ => SelectedProfileId);

        Assert.That(selected, Is.Not.Null);
        Assert.That(selected!.Suppressed, Is.True);
        Assert.That(selected.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));

        // An unrelated click (no selected profile for its window) passes through
        // and must not consume or consult the fake UIA adapter.
        var unrelated = ResidentPointerTargetDecision.TryDecide(
            store,
            uia,
            new NativePointerGesture(10, 10, "left", ParseWindow(UnrelatedWindowHandle)),
            selectedProfileIdResolver: _ => null);

        Assert.That(unrelated, Is.Not.Null);
        Assert.That(unrelated!.Verdict, Is.EqualTo(PointerTargetVerdict.Unrelated));
        Assert.That(unrelated.Suppressed, Is.False);
        Assert.That(uia.CallCount, Is.Zero,
            "Neither fail-closed suppression nor unrelated pass-through may call live UIA.");
    }

    [Test]
    public void EvidenceGenerationAndNormalizedIdentityReachResidentPointerOperation()
    {
        var uia = new FakeSendControlDiscovery();
        var store = new PointerTargetEvidenceStore();
        var identity = new NativeSubmitTargetIdentity(
            SnapshotGeneration: 1,
            ProfileId: SelectedProfileId,
            WindowHandle: SelectedWindowHandle);
        store.Publish(identity, PointerTargetVerdict.SelectedSend, generation: 42);

        var decision = ResidentPointerTargetDecision.TryDecide(
            store,
            uia,
            new NativePointerGesture(10, 10, "left", ParseWindow(SelectedWindowHandle)));

        Assert.That(decision, Is.Not.Null);
        Assert.That(decision!.EvidenceGeneration, Is.EqualTo(42));
        Assert.That(decision.Target.ProfileId, Is.EqualTo(SelectedProfileId));
        Assert.That(decision.Target.WindowHandle, Is.EqualTo(SelectedWindowHandle));
    }

    private static IntPtr ParseWindow(string hexHandle) =>
        IntPtr.Parse(hexHandle, System.Globalization.NumberStyles.HexNumber, null);
}
