using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using NUnit.Framework;
using System.Windows.Forms;

namespace CodexRedactionGate;

[TestFixture]
public sealed class ProtectedComposerSessionTests
{
    [Test]
    public void ReferenceSession_ReadWriteVerifyAndReplay_UsesOneTargetScopedContract()
    {
        var surface = CreateSurface("reference");
        var fixture = new DeterministicComposerFixture(surface, "original");
        var session = new ProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        var read = session.Read();
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(read.Succeeded, Is.True);
        Assert.That(read.Text, Is.EqualTo("original"));
        Assert.That(write.Succeeded, Is.True);
        Assert.That(write.Diagnostics["verification_exact"], Is.EqualTo("true"));
        Assert.That(replay.Succeeded, Is.True);
        Assert.That(fixture.CurrentText, Is.EqualTo("sanitized"));
        Assert.That(fixture.WriteCount, Is.EqualTo(1));
        Assert.That(fixture.SubmitCount, Is.EqualTo(1));
    }

    [Test]
    public void Session_TargetChangesBeforeWrite_FailsClosedBeforeWriter()
    {
        var initial = CreateSurface("initial");
        var changed = CreateSurface("changed");
        var fixture = new DeterministicComposerFixture(initial, "original", changed);
        var session = new ProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.StaleComposer));
        Assert.That(fixture.WriteCount, Is.Zero);
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(fixture.SubmitCount, Is.Zero);
    }

    [Test]
    public void Session_ForegroundRefusalDuringRevalidation_FailsClosedBeforeWriter()
    {
        var surface = CreateSurface("foreground-refusal");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            FailDiscoveryAfterRead = true
        };
        var session = new ProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var target = session.Revalidate();
        var write = session.WriteAndVerify("sanitized");

        Assert.That(target.Succeeded, Is.False);
        Assert.That(target.Status, Is.EqualTo(OsInteractionStatusIds.FocusLost));
        Assert.That(write.Succeeded, Is.False);
        Assert.That(fixture.WriteCount, Is.Zero);
    }

    [Test]
    public void Session_WriteVerificationMismatch_FailsClosedAndCannotReplay()
    {
        var surface = CreateSurface("mismatch");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            ReturnWrongTextAfterWrite = true
        };
        var session = new ProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.VerificationFailed));
        Assert.That(write.Diagnostics["verification_exact"], Is.EqualTo("false"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(fixture.SubmitCount, Is.Zero);
    }

    [Test]
    public void Session_ReplayFailure_IsTerminalAndDoesNotRepeatSideEffect()
    {
        var surface = CreateSurface("replay-failure");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            ReplaySucceeded = false
        };
        var session = new ProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var firstReplay = session.Replay();
        var secondReplay = session.Replay();

        Assert.That(firstReplay.Succeeded, Is.False);
        Assert.That(firstReplay.Status, Is.EqualTo(OsInteractionStatusIds.ReplayIndeterminate));
        Assert.That(secondReplay.Succeeded, Is.False);
        Assert.That(secondReplay.Status, Is.EqualTo("invalid_transition"));
        Assert.That(fixture.SubmitCount, Is.EqualTo(1));
    }

    [Test]
    public void Session_AccessException_IsTypedFailureAndCannotContinue()
    {
        var surface = CreateSurface("exception");
        var fixture = new DeterministicComposerFixture(surface, "original")
        {
            ThrowOnWrite = true
        };
        var session = new ProtectedComposerSessionFactory(
            fixture,
            fixture,
            fixture,
            fixture).Create();

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.WriteFailed));
        Assert.That(write.Diagnostics["failed_closed"], Is.EqualTo("true"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(fixture.SubmitCount, Is.Zero);
    }

    [Test]
    public void WindowsSessionFactory_UsesVerifiedComposerAdapterWithSameContract()
    {
        var surface = CreateWindowsSurface();
        var scenario = new ProductionShapedWindowsScenario(surface, "original");
        var session = scenario.CreateSession(new ExecutingStaBoundary());

        Assert.That(session.Read().Succeeded, Is.True);
        Assert.That(session.WriteAndVerify("sanitized").Succeeded, Is.True);
        Assert.That(session.Replay().Succeeded, Is.True);
        Assert.That(scenario.CurrentText, Is.EqualTo("sanitized"));
        Assert.That(scenario.SubmitCount, Is.EqualTo(1));
        Assert.That(scenario.NativeOperationInvocationCount, Is.EqualTo(4));
        Assert.That(scenario.NativeDiscoveryCount, Is.EqualTo(4));
        Assert.That(scenario.ReplayInvocationCount, Is.EqualTo(1));
        Assert.That(
            scenario.ObservedOperations,
            Is.EqualTo(new[] { "read", "write", "read", "replay" }));
        Assert.That(
            scenario.ObservedTargetSurfaceIds,
            Is.EqualTo(new[] { surface.SurfaceId, surface.SurfaceId, surface.SurfaceId, surface.SurfaceId }));
        Assert.That(scenario.ObservedWriteTexts, Is.EqualTo(new[] { "sanitized" }));
        Assert.That(scenario.ObservedReplayBindings, Is.EqualTo(new[] { "^{ENTER}" }));
    }

    [TestCase("reference")]
    [TestCase("windows")]
    public void SessionMatrix_TargetChange_FailsClosedBeforeWriteForEachAccessBoundary(string boundary)
    {
        var initial = CreateSurface("matrix-initial");
        var fixture = new DeterministicComposerFixture(initial, "original", CreateSurface("matrix-changed"));
        var scenario = boundary == "windows"
            ? new ProductionShapedWindowsScenario(initial, "original", CreateSurface("matrix-changed"))
            : null;
        var session = scenario?.CreateSession(new ExecutingStaBoundary()) ?? CreateSession(boundary, fixture);

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.StaleComposer));
        Assert.That(scenario?.WriteCount ?? fixture.WriteCount, Is.Zero);
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(scenario?.SubmitCount ?? fixture.SubmitCount, Is.Zero);
        Assert.That(scenario?.NativeOperationInvocationCount ?? 1, Is.EqualTo(1));
        Assert.That(scenario?.NativeDiscoveryCount ?? 1, Is.EqualTo(1));
    }

    [TestCase("reference")]
    [TestCase("windows")]
    public void SessionMatrix_FocusRefusal_FailsClosedBeforeWriteForEachAccessBoundary(string boundary)
    {
        var fixture = new DeterministicComposerFixture(CreateSurface("matrix-focus"), "original")
        {
            FailDiscoveryAfterRead = true
        };
        var scenario = boundary == "windows"
            ? new ProductionShapedWindowsScenario(CreateSurface("matrix-focus"), "original")
            {
                FailDiscoveryAfterRead = true
            }
            : null;
        var session = scenario?.CreateSession(new ExecutingStaBoundary()) ?? CreateSession(boundary, fixture);

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");

        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.FocusLost));
        Assert.That(scenario?.WriteCount ?? fixture.WriteCount, Is.Zero);
        Assert.That(scenario?.SubmitCount ?? fixture.SubmitCount, Is.Zero);
        Assert.That(scenario?.NativeOperationInvocationCount ?? 1, Is.EqualTo(1));
        Assert.That(scenario?.NativeDiscoveryCount ?? 1, Is.EqualTo(1));
    }

    [TestCase("reference")]
    [TestCase("windows")]
    public void SessionMatrix_StaCreationFailure_FailedReadMakesWriteAndReplayInvalidWithNoSideEffects(
        string boundary)
    {
        AssertStaFailureIsTerminal(boundary, StaFailureKind.Creation);
    }

    [TestCase("reference")]
    [TestCase("windows")]
    public void SessionMatrix_StaExecutionFailure_FailedReadMakesWriteAndReplayInvalidWithNoSideEffects(
        string boundary)
    {
        AssertStaFailureIsTerminal(boundary, StaFailureKind.Execution);
    }

    private static void AssertStaFailureIsTerminal(string boundary, StaFailureKind failureKind)
    {
        var fixture = new DeterministicComposerFixture(CreateSurface("matrix-sta"), "original");
        var referenceStaExecution = new StaFailureBoundary(failureKind);
        var referenceAccess = new StaBoundReferenceAccess(fixture, referenceStaExecution);
        var scenario = boundary == "windows"
            ? new ProductionShapedWindowsScenario(CreateWindowsSurface(), "original")
            : null;
        IProtectedComposerSession session = boundary == "reference"
            ? new ProtectedComposerSessionFactory(
                fixture,
                referenceAccess,
                fixture,
                fixture).Create()
            : scenario!.CreateSession(new StaFailureBoundary(failureKind));

        var read = session.Read();
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(read.Succeeded, Is.False);
        Assert.That(read.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        Assert.That(
            read.Diagnostics["sta_failure"],
            Is.EqualTo(failureKind == StaFailureKind.Creation ? "creation" : "execution"));
        Assert.That(read.Diagnostics["failed_closed"], Is.EqualTo("true"));
        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo("invalid_transition"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(replay.Status, Is.EqualTo("invalid_transition"));
        Assert.That(scenario?.WriteCount ?? fixture.WriteCount, Is.Zero);
        Assert.That(scenario?.SubmitCount ?? fixture.SubmitCount, Is.Zero);
        Assert.That(scenario?.NativeDiscoveryCount ?? 0, Is.Zero, "Native UIA target discovery must not run after STA failure");
        Assert.That(referenceAccess.OperationInvocationCount, Is.Zero);
        if (boundary == "reference")
        {
            Assert.That(referenceStaExecution.InvocationCount, Is.EqualTo(1));
        }
    }

    [TestCase("reference")]
    [TestCase("windows")]
    public void SessionMatrix_WriteVerificationMismatch_BlocksReplayForEachAccessBoundary(string boundary)
    {
        var fixture = new DeterministicComposerFixture(CreateSurface("matrix-mismatch"), "original")
        {
            ReturnWrongTextAfterWrite = true
        };
        var scenario = boundary == "windows"
            ? new ProductionShapedWindowsScenario(CreateSurface("matrix-mismatch"), "original")
            {
                ReturnWrongTextAfterWrite = true
            }
            : null;
        var session = scenario?.CreateSession(new ExecutingStaBoundary()) ?? CreateSession(boundary, fixture);

        Assert.That(session.Read().Succeeded, Is.True);
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.VerificationFailed));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(scenario?.SubmitCount ?? fixture.SubmitCount, Is.Zero);
        Assert.That(scenario?.NativeOperationInvocationCount ?? 3, Is.EqualTo(3));
        Assert.That(scenario?.NativeDiscoveryCount ?? 3, Is.EqualTo(3));
    }

    [TestCase("reference", OsInteractionStatusIds.ReplayUnavailable)]
    [TestCase("reference", OsInteractionStatusIds.ReplayIndeterminate)]
    [TestCase("windows", OsInteractionStatusIds.ReplayUnavailable)]
    [TestCase("windows", OsInteractionStatusIds.ReplayIndeterminate)]
    public void SessionMatrix_ReplayOutcomesRemainDistinctAndTerminalForEachAccessBoundary(
        string boundary,
        string expectedStatus)
    {
        var fixture = new DeterministicComposerFixture(CreateSurface("matrix-replay"), "original")
        {
            ReplaySucceeded = false,
            ReplayFailureStatus = expectedStatus
        };
        var scenario = boundary == "windows"
            ? new ProductionShapedWindowsScenario(CreateSurface("matrix-replay"), "original")
            {
                ReplaySucceeded = false,
                ReplayFailureStatus = expectedStatus
            }
            : null;
        var session = scenario?.CreateSession(new ExecutingStaBoundary()) ?? CreateSession(boundary, fixture);

        Assert.That(session.Read().Succeeded, Is.True);
        var firstReplay = session.Replay();
        var secondReplay = session.Replay();

        Assert.That(firstReplay.Status, Is.EqualTo(expectedStatus));
        Assert.That(secondReplay.Status, Is.EqualTo("invalid_transition"));
        Assert.That(scenario?.SubmitCount ?? fixture.SubmitCount, Is.EqualTo(1));
        Assert.That(scenario?.NativeOperationInvocationCount ?? 2, Is.EqualTo(2));
        Assert.That(scenario?.NativeDiscoveryCount ?? 2, Is.EqualTo(2));
        Assert.That(scenario?.ReplayInvocationCount ?? 1, Is.EqualTo(1));
    }

    [Test]
    public void NativeAccess_StaCreationFailure_FailsCaptureBeforeTargetAccess()
    {
        var discoveryCalls = 0;
        var access = new NativeVerifiedComposerTextAccess(
            () =>
            {
                discoveryCalls++;
                return TextSurfaceDiscoveryResult.Success(CreateWindowsSurface());
            },
            staExecution: new StaFailureBoundary(StaFailureKind.Creation));

        var result = access.CaptureText(CreateWindowsSurface());

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        Assert.That(result.Diagnostics["sta_failure"], Is.EqualTo("creation"));
        Assert.That(discoveryCalls, Is.Zero);
    }

    [Test]
    public void NativeAccess_EmptyResolvedReadRemainsFailClosed()
    {
        var access = new NativeVerifiedComposerTextAccess(
            () => TextSurfaceDiscoveryResult.Success(CreateWindowsSurface()),
            staExecution: new ExecutingStaBoundary(),
            targetOperations: new EmptyResolvedReadTargetOperations());

        var result = access.CaptureText(CreateWindowsSurface());

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        Assert.That(result.Text, Is.Null);
        Assert.That(result.Diagnostics["native_capture_failure_kind"], Is.EqualTo("empty_text"));
        Assert.That(result.Diagnostics["native_capture_strategy"], Is.EqualTo("unavailable"));
    }

    [Test]
    public void NativeAccess_StaExecutionFailure_FailsWriteBeforeTargetAccess()
    {
        var discoveryCalls = 0;
        var access = new NativeVerifiedComposerTextAccess(
            () =>
            {
                discoveryCalls++;
                return TextSurfaceDiscoveryResult.Success(CreateWindowsSurface());
            },
            staExecution: new StaFailureBoundary(StaFailureKind.Execution));

        var result = access.ReplaceText(CreateWindowsSurface(), "sanitized");

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.WriteFailed));
        Assert.That(result.Diagnostics["sta_failure"], Is.EqualTo("execution"));
        Assert.That(discoveryCalls, Is.Zero);
    }

    [TestCaseSource(nameof(CompleteClipboardDataObjects))]
    public void NativeAccess_KeyboardFallback_PreservesTypedClipboardAcrossCopyAndPaste(IDataObject payload)
    {
        var clipboard = new DeterministicClipboardBoundary(payload);
        var keyboard = new DeterministicKeyboardClipboardOperations(clipboard);
        var targetOperations = new KeyboardFallbackTargetOperations(keyboard);
        var access = new NativeVerifiedComposerTextAccess(
            () => TextSurfaceDiscoveryResult.Success(CreateKeyboardFallbackWindowsSurface()),
            staExecution: new ExecutingStaBoundary(),
            targetOperations: targetOperations,
            clipboard: clipboard,
            keyboardClipboardOperations: keyboard);

        var capture = access.CaptureText(CreateKeyboardFallbackWindowsSurface());
        var replace = access.ReplaceText(CreateKeyboardFallbackWindowsSurface(), "sanitized composer text");

        Assert.That(capture.Succeeded, Is.True);
        Assert.That(capture.Text, Is.EqualTo("composer text"));
        Assert.That(replace.Succeeded, Is.True);
        AssertClipboardDataEqual(payload, clipboard.CurrentData);
        Assert.That(clipboard.Calls, Is.EqualTo(new[]
        {
            "capture", "clear", "read", "restore",
            "capture", "set-text", "clear", "read", "restore"
        }));
        Assert.That(keyboard.Calls, Is.EqualTo(new[] { "copy:target", "paste:target", "copy:target" }));
        Assert.That(targetOperations.FallbackDecisionCount, Is.EqualTo(2));
        Assert.That(targetOperations.ReadCount, Is.EqualTo(1));
        Assert.That(targetOperations.WriteCount, Is.EqualTo(1));
    }

    [Test]
    public void NativeAccess_KeyboardFallback_RestoresAfterTemporaryOperationFailure()
    {
        var payload = CreateImageDataObject();
        var clipboard = new DeterministicClipboardBoundary(payload);
        var keyboard = new DeterministicKeyboardClipboardOperations(clipboard);
        var targetOperations = new KeyboardFallbackTargetOperations(keyboard) { FailCopy = true };
        var access = new NativeVerifiedComposerTextAccess(
            () => TextSurfaceDiscoveryResult.Success(CreateKeyboardFallbackWindowsSurface()),
            staExecution: new ExecutingStaBoundary(),
            targetOperations: targetOperations,
            clipboard: clipboard,
            keyboardClipboardOperations: keyboard);

        var result = access.CaptureText(CreateKeyboardFallbackWindowsSurface());

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        AssertClipboardDataEqual(payload, clipboard.CurrentData);
        Assert.That(clipboard.RestoreAttemptCount, Is.EqualTo(1));
        Assert.That(targetOperations.ReadCount, Is.EqualTo(1));
    }

    [Test]
    public void NativeAccess_KeyboardFallback_RetriesRestoreOnceAndRestoresTypedFileList()
    {
        var payload = CreateFileListDataObject();
        var clipboard = new DeterministicClipboardBoundary(payload) { RestoreFailuresBeforeSuccess = 1 };
        var keyboard = new DeterministicKeyboardClipboardOperations(clipboard);
        var access = new NativeVerifiedComposerTextAccess(
            () => TextSurfaceDiscoveryResult.Success(CreateKeyboardFallbackWindowsSurface()),
            staExecution: new ExecutingStaBoundary(),
            targetOperations: new KeyboardFallbackTargetOperations(keyboard),
            clipboard: clipboard,
            keyboardClipboardOperations: keyboard);

        var result = access.CaptureText(CreateKeyboardFallbackWindowsSurface());

        Assert.That(result.Succeeded, Is.True);
        AssertClipboardDataEqual(payload, clipboard.CurrentData);
        Assert.That(clipboard.Calls, Is.EqualTo(new[] { "capture", "clear", "read", "restore", "restore" }));
        Assert.That(clipboard.RestoreAttemptCount, Is.EqualTo(2));
    }

    [Test]
    public void NativeAccess_KeyboardFallback_LockedClipboardFailsClosedWithoutTargetAccess()
    {
        var clipboard = new DeterministicClipboardBoundary(CreateMixedCustomDataObject("secret clipboard"))
        {
            CaptureFails = true
        };
        var scenario = new ProductionShapedWindowsScenario(CreateKeyboardFallbackWindowsSurface(), "original");
        var session = scenario.CreateSession(new ExecutingStaBoundary(), clipboard);

        var result = session.Read();
        var replay = session.Replay();

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        Assert.That(result.Text, Is.Null);
        Assert.That(result.Diagnostics["clipboard_failure"], Is.EqualTo("capture"));
        Assert.That(string.Join("|", result.Diagnostics.Values), Does.Not.Contain("secret clipboard"));
        Assert.That(scenario.NativeOperationInvocationCount, Is.Zero);
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(scenario.SubmitCount, Is.Zero);
        Assert.That(clipboard.RestoreAttemptCount, Is.Zero);
    }

    [Test]
    public void WindowsSession_ClipboardRestoreFailureIsRawFreeAndSuppressesReplay()
    {
        var surface = CreateKeyboardFallbackWindowsSurface();
        var clipboard = new DeterministicClipboardBoundary(CreateFileListDataObject("C:\\secret.txt"))
        {
            RestoreAlwaysFails = true
        };
        var scenario = new ProductionShapedWindowsScenario(surface, "original");
        var session = scenario.CreateSession(new ExecutingStaBoundary(), clipboard);

        var read = session.Read();
        var replay = session.Replay();

        Assert.That(read.Succeeded, Is.False);
        Assert.That(read.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        Assert.That(read.Text, Is.Null);
        Assert.That(read.Diagnostics["clipboard_failure"], Is.EqualTo("restore"));
        Assert.That(string.Join("|", read.Diagnostics.Values), Does.Not.Contain("C:\\secret.txt"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(scenario.SubmitCount, Is.Zero);
        Assert.That(clipboard.Calls, Is.EqualTo(new[] { "capture", "clear", "read", "restore", "restore" }));
        Assert.That(clipboard.RestoreAttemptCount, Is.EqualTo(2));
    }

    [Test]
    public void WindowsSession_ClipboardRestoreFailureAfterThrowingCaptureIsRawFreeAndSuppressesReplay()
    {
        var clipboard = new DeterministicClipboardBoundary(CreateMixedCustomDataObject("secret clipboard"))
        {
            RestoreAlwaysFails = true
        };
        var scenario = new ProductionShapedWindowsScenario(CreateKeyboardFallbackWindowsSurface(), "original")
        {
            ThrowOnRead = true
        };
        var session = scenario.CreateSession(new ExecutingStaBoundary(), clipboard);

        var read = session.Read();
        var replay = session.Replay();

        Assert.That(read.Succeeded, Is.False);
        Assert.That(read.Status, Is.EqualTo(OsInteractionStatusIds.CaptureFailed));
        Assert.That(read.Text, Is.Null);
        Assert.That(read.Diagnostics["clipboard_failure"], Is.EqualTo("restore"));
        Assert.That(string.Join("|", read.Diagnostics.Values), Does.Not.Contain("secret clipboard"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(scenario.SubmitCount, Is.Zero);
        Assert.That(clipboard.RestoreAttemptCount, Is.EqualTo(2));
    }

    [Test]
    public void WindowsSession_ClipboardRestoreFailureAfterThrowingReplaceIsRawFreeAndSuppressesReplay()
    {
        var clipboard = new DeterministicClipboardBoundary(CreateFileListDataObject("C:\\secret.txt"));
        var scenario = new ProductionShapedWindowsScenario(CreateKeyboardFallbackWindowsSurface(), "original")
        {
            ThrowOnWrite = true
        };
        var session = scenario.CreateSession(new ExecutingStaBoundary(), clipboard);

        Assert.That(session.Read().Succeeded, Is.True);
        clipboard.RestoreAlwaysFails = true;
        var write = session.WriteAndVerify("sanitized");
        var replay = session.Replay();

        Assert.That(write.Succeeded, Is.False);
        Assert.That(write.Status, Is.EqualTo(OsInteractionStatusIds.WriteFailed));
        Assert.That(write.Diagnostics["clipboard_failure"], Is.EqualTo("restore"));
        Assert.That(string.Join("|", write.Diagnostics.Values), Does.Not.Contain("C:\\secret.txt"));
        Assert.That(replay.Succeeded, Is.False);
        Assert.That(scenario.SubmitCount, Is.Zero);
        Assert.That(clipboard.RestoreAttemptCount, Is.EqualTo(3));
    }

    [Test]
    public void NativeStaExecutionBoundary_StartedActionCannotReturnTimeoutBeforeIrreversibleEffect()
    {
        var boundary = new NativeStaExecutionBoundary(TimeSpan.FromSeconds(1));
        using var actionStarted = new ManualResetEventSlim();
        using var allowIrreversibleEffect = new ManualResetEventSlim();
        using var irreversibleEffectAttempted = new ManualResetEventSlim();
        using var executeReturned = new ManualResetEventSlim();
        StaExecutionResult<int>? result = null;
        var caller = new Thread(() =>
        {
            result = boundary.Execute(
                () =>
                {
                    actionStarted.Set();
                    allowIrreversibleEffect.Wait();
                    irreversibleEffectAttempted.Set();
                    return 17;
                });
            executeReturned.Set();
        });

        caller.Start();
        Assert.That(actionStarted.Wait(TimeSpan.FromSeconds(5)), Is.True, "the real STA action must begin");

        bool returnedBeforeEffect;
        try
        {
            returnedBeforeEffect = executeReturned.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            allowIrreversibleEffect.Set();
            Assert.That(caller.Join(TimeSpan.FromSeconds(5)), Is.True, "the worker caller must terminate");
        }

        Assert.That(
            returnedBeforeEffect,
            Is.False,
            "a started action cannot publish Timeout while its irreversible effect can still occur later");
        Assert.That(irreversibleEffectAttempted.IsSet, Is.True);
        Assert.That(result, Is.EqualTo(StaExecutionResult<int>.Success(17)));
    }

    private static TextSurfaceDescriptor CreateSurface(string id)
    {
        return new TextSurfaceDescriptor(
            $"session:{id}",
            "reference-ai",
            "Reference AI",
            true,
            true,
            true,
            true,
            new SurfaceMetadata(
                SurfaceKind: "reference",
                ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                ArbitraryMetadata: new Dictionary<string, string>
                {
                    ["focused_element_hash"] = "reference-element",
                    ["submit_binding"] = "Ctrl+Enter",
                    ["submit_binding_sendkeys"] = "^{ENTER}"
                }));
    }

    private static TextSurfaceDescriptor CreateWindowsSurface()
    {
        return new TextSurfaceDescriptor(
            "session:windows",
            "codex-desktop",
            "OpenAI Desktop",
            true,
            true,
            true,
            true,
            new SurfaceMetadata(
                SurfaceKind: "windows",
                ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                ArbitraryMetadata: new Dictionary<string, string>
                {
                    ["focused_element_hash"] = "verified-element",
                    ["submit_binding"] = "Ctrl+Enter",
                    ["submit_binding_sendkeys"] = "^{ENTER}"
                }));
    }

    private static TextSurfaceDescriptor CreateKeyboardFallbackWindowsSurface()
    {
        var surface = CreateWindowsSurface();
        var metadata = new Dictionary<string, string>(
            surface.Metadata.ArbitraryMetadata ?? new Dictionary<string, string>(),
            StringComparer.Ordinal)
        {
            ["keyboard_write_fallback"] = "true"
        };
        return surface with { Metadata = surface.Metadata with { ArbitraryMetadata = metadata } };
    }

    private static IEnumerable<TestCaseData> CompleteClipboardDataObjects()
    {
        yield return new TestCaseData(CreateTextDataObject()).SetName("NativeAccess_KeyboardFallback_PreservesTextClipboardAcrossCopyAndPaste");
        yield return new TestCaseData(CreateImageDataObject()).SetName("NativeAccess_KeyboardFallback_PreservesImageClipboardAcrossCopyAndPaste");
        yield return new TestCaseData(CreateFileListDataObject()).SetName("NativeAccess_KeyboardFallback_PreservesFileListClipboardAcrossCopyAndPaste");
        yield return new TestCaseData(CreateMixedCustomDataObject("custom-payload")).SetName("NativeAccess_KeyboardFallback_PreservesMixedCustomClipboardAcrossCopyAndPaste");
        yield return new TestCaseData(new DataObject()).SetName("NativeAccess_KeyboardFallback_PreservesEmptyClipboardAcrossCopyAndPaste");
    }

    private static IDataObject CreateTextDataObject(string value = "clipboard text")
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, value);
        return data;
    }

    private static IDataObject CreateImageDataObject()
    {
        var data = new DataObject();
        var image = new Bitmap(2, 1);
        image.SetPixel(0, 0, Color.Red);
        image.SetPixel(1, 0, Color.Blue);
        data.SetData(DataFormats.Bitmap, image);
        return data;
    }

    private static IDataObject CreateFileListDataObject(string path = "C:\\report.pdf")
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { path, "C:\\second.txt" });
        return data;
    }

    private static IDataObject CreateMixedCustomDataObject(string customPayload)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "clipboard text");
        data.SetData(DataFormats.FileDrop, new[] { "C:\\report.pdf" });
        data.SetData("application/x-codex-custom", new byte[] { 1, 2, 3, 4 });
        data.SetData("application/x-codex-custom-name", customPayload);
        return data;
    }

    private static void AssertClipboardDataEqual(IDataObject expected, IDataObject actual)
    {
        var expectedFormats = expected.GetFormats(autoConvert: false).OrderBy(format => format, StringComparer.Ordinal).ToArray();
        var actualFormats = actual.GetFormats(autoConvert: false).OrderBy(format => format, StringComparer.Ordinal).ToArray();
        Assert.That(actualFormats, Is.EqualTo(expectedFormats));
        foreach (var format in expectedFormats)
        {
            var expectedValue = expected.GetData(format, autoConvert: false);
            var actualValue = actual.GetData(format, autoConvert: false);
            switch (expectedValue)
            {
                case Bitmap expectedBitmap when actualValue is Bitmap actualBitmap:
                    Assert.That(actualBitmap.Width, Is.EqualTo(expectedBitmap.Width));
                    Assert.That(actualBitmap.Height, Is.EqualTo(expectedBitmap.Height));
                    for (var x = 0; x < expectedBitmap.Width; x++)
                    {
                        for (var y = 0; y < expectedBitmap.Height; y++)
                        {
                            Assert.That(actualBitmap.GetPixel(x, y), Is.EqualTo(expectedBitmap.GetPixel(x, y)));
                        }
                    }
                    break;
                case string[] expectedPaths:
                    Assert.That(actualValue, Is.EqualTo(expectedPaths));
                    break;
                case byte[] expectedBytes:
                    Assert.That(actualValue, Is.EqualTo(expectedBytes));
                    break;
                default:
                    Assert.That(actualValue, Is.EqualTo(expectedValue));
                    break;
            }
        }
    }

    private static IProtectedComposerSession CreateSession(
        string boundary,
        DeterministicComposerFixture fixture)
    {
        return boundary == "reference"
            ? new ProtectedComposerSessionFactory(fixture, fixture, fixture, fixture).Create()
            : throw new InvalidOperationException("Windows matrix requires ProductionShapedWindowsScenario.");
    }

    private sealed class DeterministicComposerFixture :
        IActiveTextSurfaceDiscovery,
        ITextSurfaceReader,
        ITextSurfaceWriter,
        ISubmitAction,
        IVerifiedComposerTextAccess
    {
        private readonly TextSurfaceDescriptor _initialSurface;
        private readonly TextSurfaceDescriptor? _nextSurface;

        public DeterministicComposerFixture(
            TextSurfaceDescriptor initialSurface,
            string initialText,
            TextSurfaceDescriptor? nextSurface = null)
        {
            _initialSurface = initialSurface;
            _nextSurface = nextSurface;
            CurrentText = initialText;
        }

        public string CurrentText { get; private set; }

        public int WriteCount { get; private set; }

        public int SubmitCount { get; private set; }

        public bool ReturnWrongTextAfterWrite { get; init; }

        public bool ReplaySucceeded { get; init; } = true;

        public string ReplayFailureStatus { get; init; } = OsInteractionStatusIds.ReplayIndeterminate;

        public bool ThrowOnWrite { get; init; }

        public bool FailDiscoveryAfterRead { get; init; }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            if (FailDiscoveryAfterRead && DiscoveryCount > 0)
            {
                DiscoveryCount++;
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }

            var surface = _nextSurface is not null && WriteCount == 0 && SubmitCount == 0 && DiscoveryCount > 0
                ? _nextSurface
                : _initialSurface;
            DiscoveryCount++;
            return TextSurfaceDiscoveryResult.Success(surface);
        }

        public int DiscoveryCount { get; private set; }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
        {
            var text = ReturnWrongTextAfterWrite && WriteCount > 0 ? "unexpected" : CurrentText;
            return new TextCaptureResult(true, "captured", text, new Dictionary<string, string>());
        }

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("test write failure");
            }

            CurrentText = text;
            WriteCount++;
            return new TextReplacementResult(true, OsInteractionStatusIds.Applied, new Dictionary<string, string>());
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            SubmitCount++;
            return new SubmitActionResult(
                ReplaySucceeded,
                ReplaySucceeded ? OsInteractionStatusIds.Submitted : ReplayFailureStatus,
                new Dictionary<string, string>());
        }
    }

    private sealed class StaFailureBoundary : IStaExecutionBoundary
    {
        private readonly StaFailureKind _failureKind;

        public StaFailureBoundary(StaFailureKind failureKind)
        {
            _failureKind = failureKind;
        }

        public int InvocationCount { get; private set; }

        public StaExecutionResult<T> Execute<T>(Func<T> action)
        {
            InvocationCount++;
            return StaExecutionResult<T>.Failure(_failureKind);
        }
    }

    private sealed class ExecutingStaBoundary : IStaExecutionBoundary
    {
        public StaExecutionResult<T> Execute<T>(Func<T> action) =>
            StaExecutionResult<T>.Success(action());
    }

    private sealed class DeterministicClipboardBoundary : IClipboardAccessBoundary
    {
        private readonly IDataObject _snapshotData;

        public DeterministicClipboardBoundary(IDataObject initialData)
        {
            _snapshotData = initialData;
            CurrentData = initialData;
        }

        public IDataObject CurrentData { get; private set; }

        public List<string> Calls { get; } = new();

        public int CaptureCount { get; private set; }

        public int RestoreAttemptCount { get; private set; }

        public bool CaptureFails { get; init; }

        public bool RestoreAlwaysFails { get; set; }

        public int RestoreFailuresBeforeSuccess { get; init; }

        public ClipboardSnapshotCapture CaptureSnapshot()
        {
            CaptureCount++;
            Calls.Add("capture");
            return CaptureFails
                ? ClipboardSnapshotCapture.Failure()
                : ClipboardSnapshotCapture.Success(new DeterministicClipboardSnapshot(_snapshotData));
        }

        public ClipboardBoundaryResult SetText(string text)
        {
            Calls.Add("set-text");
            CurrentData = CreateTextDataObject(text);
            return ClipboardBoundaryResult.Success();
        }

        public ClipboardBoundaryResult Clear()
        {
            Calls.Add("clear");
            CurrentData = new DataObject();
            return ClipboardBoundaryResult.Success();
        }

        public ClipboardTextRead ReadUnicodeText()
        {
            Calls.Add("read");
            return CurrentData.GetDataPresent(DataFormats.UnicodeText, autoConvert: false)
                ? ClipboardTextRead.Success((string)CurrentData.GetData(DataFormats.UnicodeText, autoConvert: false)!)
                : ClipboardTextRead.Failure();
        }

        public void SimulateCopy(string text) => CurrentData = CreateTextDataObject(text);

        public ClipboardBoundaryResult Restore(IClipboardSnapshot snapshot)
        {
            RestoreAttemptCount++;
            Calls.Add("restore");
            if (RestoreAlwaysFails || RestoreAttemptCount <= RestoreFailuresBeforeSuccess)
            {
                return ClipboardBoundaryResult.Failure();
            }

            CurrentData = ((DeterministicClipboardSnapshot)snapshot).Data;
            return ClipboardBoundaryResult.Success();
        }

        private sealed record DeterministicClipboardSnapshot(IDataObject Data) : IClipboardSnapshot;
    }

    private sealed class DeterministicKeyboardClipboardOperations : IKeyboardClipboardOperations
    {
        private readonly DeterministicClipboardBoundary _clipboard;

        public DeterministicKeyboardClipboardOperations(DeterministicClipboardBoundary clipboard)
        {
            _clipboard = clipboard;
        }

        public List<string> Calls { get; } = new();

        public string CopiedText { get; set; } = "composer text";

        public bool CopyFails { get; set; }

        public string? Copy(IKeyboardFallbackComposerTarget target)
        {
            Calls.Add("copy:target");
            _ = _clipboard.Clear();
            if (CopyFails)
            {
                return null;
            }

            _clipboard.SimulateCopy(CopiedText);
            return _clipboard.ReadUnicodeText().Text;
        }

        public bool Paste(IKeyboardFallbackComposerTarget target, string text)
        {
            Calls.Add("paste:target");
            _ = _clipboard.SetText(text);
            CopiedText = text;
            return true;
        }
    }

    private sealed class KeyboardFallbackTargetOperations : INativeVerifiedComposerTargetOperations
    {
        private readonly KeyboardFallbackTarget _target = new();
        private readonly DeterministicKeyboardClipboardOperations _keyboard;

        public KeyboardFallbackTargetOperations(DeterministicKeyboardClipboardOperations keyboard)
        {
            _keyboard = keyboard;
        }

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public int FallbackDecisionCount => _target.DecisionCount;

        public bool FailCopy { get; init; }

        public INativeVerifiedComposerTarget? Reacquire(TextSurfaceDescriptor surface) => _target;

        public NativeComposerTextReadAttempt ReadText(
            INativeVerifiedComposerTarget target,
            TextSurfaceDescriptor surface)
        {
            ReadCount++;
            _keyboard.CopyFails = FailCopy;
            return NativeComposerKeyboardFallback.TryGetTarget(target, surface, out var fallbackTarget)
                ? NativeComposerKeyboardFallback.Capture(fallbackTarget, _keyboard, _keyboard.CopiedText)
                : new NativeComposerTextReadAttempt(false, null, "unavailable", new Dictionary<string, string>());
        }

        public NativeComposerTextWriteAttempt WriteText(
            INativeVerifiedComposerTarget target,
            TextSurfaceDescriptor surface,
            string text)
        {
            WriteCount++;
            return NativeComposerKeyboardFallback.TryGetTarget(target, surface, out var fallbackTarget)
                ? NativeComposerKeyboardFallback.Paste(fallbackTarget, _keyboard, text)
                : new NativeComposerTextWriteAttempt(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
        }

        public VerifiedComposerReplayResult Replay(
            INativeVerifiedComposerTarget target,
            string sendKeysText) =>
            new(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string>());

        private sealed class KeyboardFallbackTarget : IKeyboardFallbackComposerTarget
        {
            public int DecisionCount { get; private set; }

            public bool CanUseKeyboardFallback(TextSurfaceDescriptor surface)
            {
                DecisionCount++;
                return surface.Metadata.TryGetValue("keyboard_write_fallback") == "true";
            }
        }
    }

    private sealed class EmptyResolvedReadTargetOperations : INativeVerifiedComposerTargetOperations
    {
        private static readonly INativeVerifiedComposerTarget Target = new EmptyResolvedReadTarget();

        public INativeVerifiedComposerTarget? Reacquire(TextSurfaceDescriptor surface) => Target;

        public NativeComposerTextReadAttempt ReadText(
            INativeVerifiedComposerTarget target,
            TextSurfaceDescriptor surface) =>
            new(
                true,
                string.Empty,
                "unavailable",
                new Dictionary<string, string>());

        public NativeComposerTextWriteAttempt WriteText(
            INativeVerifiedComposerTarget target,
            TextSurfaceDescriptor surface,
            string text) =>
            throw new InvalidOperationException("Write must not run during capture regression.");

        public VerifiedComposerReplayResult Replay(
            INativeVerifiedComposerTarget target,
            string sendKeysText) =>
            throw new InvalidOperationException("Replay must not run during capture regression.");

        private sealed class EmptyResolvedReadTarget : INativeVerifiedComposerTarget
        {
        }
    }

    private sealed class StaBoundReferenceAccess : ITextSurfaceReader
    {
        private readonly DeterministicComposerFixture _fixture;
        private readonly IStaExecutionBoundary _staExecution;

        public StaBoundReferenceAccess(
            DeterministicComposerFixture fixture,
            IStaExecutionBoundary staExecution)
        {
            _fixture = fixture;
            _staExecution = staExecution;
        }

        public int OperationInvocationCount { get; private set; }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
        {
            var execution = _staExecution.Execute(() =>
            {
                OperationInvocationCount++;
                return _fixture.CaptureText(surface);
            });
            return execution.Succeeded
                ? execution.Value!
                : new TextCaptureResult(
                    false,
                    OsInteractionStatusIds.CaptureFailed,
                    null,
                    new Dictionary<string, string>
                    {
                        ["sta_failure"] = execution.FailureKind == StaFailureKind.Creation ? "creation" : "execution",
                        ["failed_closed"] = "true"
                    });
        }
    }

    private sealed class ProductionShapedWindowsScenario : IActiveTextSurfaceDiscovery
    {
        private readonly TextSurfaceDescriptor _initialSurface;
        private readonly TextSurfaceDescriptor? _nextSurface;

        public ProductionShapedWindowsScenario(
            TextSurfaceDescriptor initialSurface,
            string initialText,
            TextSurfaceDescriptor? nextSurface = null)
        {
            _initialSurface = initialSurface;
            _nextSurface = nextSurface;
            CurrentText = initialText;
        }

        public string CurrentText { get; private set; }

        public int NativeDiscoveryCount { get; private set; }

        public int WriteCount { get; private set; }

        public int SubmitCount { get; private set; }

        public int NativeOperationInvocationCount { get; private set; }

        public int ReplayInvocationCount { get; private set; }

        public List<string> ObservedOperations { get; } = new();

        public List<string> ObservedTargetSurfaceIds { get; } = new();

        public List<string> ObservedWriteTexts { get; } = new();

        public List<string> ObservedReplayBindings { get; } = new();

        public bool FailDiscoveryAfterRead { get; init; }

        public bool ReturnWrongTextAfterWrite { get; init; }

        public bool ThrowOnRead { get; init; }

        public bool ThrowOnWrite { get; init; }

        public bool ReplaySucceeded { get; init; } = true;

        public string ReplayFailureStatus { get; init; } = OsInteractionStatusIds.ReplayIndeterminate;

        public IProtectedComposerSession CreateSession(
            IStaExecutionBoundary staExecution,
            IClipboardAccessBoundary? clipboard = null)
        {
            var keyboard = clipboard is DeterministicClipboardBoundary deterministicClipboard
                ? new DeterministicKeyboardClipboardOperations(deterministicClipboard)
                : null;
            var nativeAccess = new NativeVerifiedComposerTextAccess(
                () => TextSurfaceDiscoveryResult.Success(_initialSurface),
                staExecution: staExecution,
                targetOperations: new ProductionShapedNativeTargetOperations(this, keyboard),
                clipboard: clipboard,
                keyboardClipboardOperations: keyboard);
            return new WindowsProtectedComposerSessionFactory(
                this,
                new WindowsVerifiedComposerSurfaceAdapter(nativeAccess)).Create();
        }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            if (FailDiscoveryAfterRead && DiscoveryCount > 0)
            {
                DiscoveryCount++;
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }

            var surface = _nextSurface is not null && WriteCount == 0 && SubmitCount == 0 && DiscoveryCount > 0
                ? _nextSurface
                : _initialSurface;
            DiscoveryCount++;
            return TextSurfaceDiscoveryResult.Success(surface);
        }

        public int DiscoveryCount { get; private set; }

        private bool TryReacquire(TextSurfaceDescriptor surface)
        {
            NativeDiscoveryCount++;
            ObservedTargetSurfaceIds.Add(surface.SurfaceId);
            return string.Equals(surface.SurfaceId, _initialSurface.SurfaceId, StringComparison.Ordinal);
        }

        private sealed class ProductionShapedNativeTarget : IKeyboardFallbackComposerTarget
        {
            public bool CanUseKeyboardFallback(TextSurfaceDescriptor surface) =>
                surface.Metadata.TryGetValue("keyboard_write_fallback") == "true";
        }

        private sealed class ProductionShapedNativeTargetOperations : INativeVerifiedComposerTargetOperations
        {
            private static readonly IKeyboardFallbackComposerTarget Target = new ProductionShapedNativeTarget();
            private readonly ProductionShapedWindowsScenario _scenario;
            private readonly DeterministicKeyboardClipboardOperations? _keyboard;

            public ProductionShapedNativeTargetOperations(
                ProductionShapedWindowsScenario scenario,
                DeterministicKeyboardClipboardOperations? keyboard)
            {
                _scenario = scenario;
                _keyboard = keyboard;
            }

            public INativeVerifiedComposerTarget? Reacquire(TextSurfaceDescriptor surface) =>
                _scenario.TryReacquire(surface) ? Target : null;

            public NativeComposerTextReadAttempt ReadText(
                INativeVerifiedComposerTarget target,
                TextSurfaceDescriptor surface)
            {
                _scenario.NativeOperationInvocationCount++;
                _scenario.ObservedOperations.Add("read");
                if (_scenario.ThrowOnRead)
                {
                    throw new InvalidOperationException("test read failure");
                }

                var text = _scenario.ReturnWrongTextAfterWrite && _scenario.WriteCount > 0
                    ? "unexpected"
                    : _scenario.CurrentText;
                if (_keyboard is not null
                    && NativeComposerKeyboardFallback.TryGetTarget(target, surface, out var fallbackTarget))
                {
                    _keyboard.CopiedText = text;
                    return NativeComposerKeyboardFallback.Capture(fallbackTarget, _keyboard, text);
                }

                return new NativeComposerTextReadAttempt(
                    true,
                    text,
                    "deterministic-target",
                    new Dictionary<string, string>());
            }

            public NativeComposerTextWriteAttempt WriteText(
                INativeVerifiedComposerTarget target,
                TextSurfaceDescriptor surface,
                string text)
            {
                _scenario.NativeOperationInvocationCount++;
                _scenario.ObservedOperations.Add("write");
                _scenario.ObservedWriteTexts.Add(text);
                if (_scenario.ThrowOnWrite)
                {
                    throw new InvalidOperationException("test write failure");
                }

                if (_keyboard is not null
                    && NativeComposerKeyboardFallback.TryGetTarget(target, surface, out var fallbackTarget))
                {
                    var fallbackWrite = NativeComposerKeyboardFallback.Paste(fallbackTarget, _keyboard, text);
                    if (fallbackWrite.Succeeded)
                    {
                        _scenario.CurrentText = text;
                        _scenario.WriteCount++;
                    }

                    return fallbackWrite;
                }

                _scenario.CurrentText = text;
                _scenario.WriteCount++;
                return new NativeComposerTextWriteAttempt(
                    true,
                    OsInteractionStatusIds.Applied,
                    new Dictionary<string, string>());
            }

            public VerifiedComposerReplayResult Replay(
                INativeVerifiedComposerTarget target,
                string sendKeysText)
            {
                _scenario.NativeOperationInvocationCount++;
                _scenario.ObservedOperations.Add("replay");
                _scenario.SubmitCount++;
                _scenario.ReplayInvocationCount++;
                _scenario.ObservedReplayBindings.Add(sendKeysText);
                return new VerifiedComposerReplayResult(
                    _scenario.ReplaySucceeded,
                    _scenario.ReplaySucceeded
                        ? OsInteractionStatusIds.Submitted
                        : _scenario.ReplayFailureStatus,
                    new Dictionary<string, string>());
            }
        }
    }

    private sealed class StaUnavailableReader : ITextSurfaceReader
    {
        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface) =>
            new(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
    }

    private sealed class FixedSurfaceDiscovery : IActiveTextSurfaceDiscovery
    {
        private readonly TextSurfaceDescriptor _surface;

        public FixedSurfaceDiscovery(TextSurfaceDescriptor surface)
        {
            _surface = surface;
        }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface() =>
            TextSurfaceDiscoveryResult.Success(_surface);
    }

    private sealed class VerifiedComposerFixture : IVerifiedComposerTextAccess
    {
        public VerifiedComposerFixture(string initialText)
        {
            CurrentText = initialText;
        }

        public string CurrentText { get; private set; }

        public int SubmitCount { get; private set; }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface) =>
            new(true, "captured", CurrentText, new Dictionary<string, string>());

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            CurrentText = text;
            return new TextReplacementResult(true, OsInteractionStatusIds.Applied, new Dictionary<string, string>());
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            SubmitCount++;
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string>());
        }
    }
}
