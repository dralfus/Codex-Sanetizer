using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;

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

        public bool ReplaySucceeded { get; init; } = true;

        public string ReplayFailureStatus { get; init; } = OsInteractionStatusIds.ReplayIndeterminate;

        public IProtectedComposerSession CreateSession(IStaExecutionBoundary staExecution)
        {
            var nativeAccess = new NativeVerifiedComposerTextAccess(
                () => TextSurfaceDiscoveryResult.Success(_initialSurface),
                staExecution: staExecution,
                targetOperations: new ProductionShapedNativeTargetOperations(this));
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

        private sealed class ProductionShapedNativeTarget : INativeVerifiedComposerTarget
        {
        }

        private sealed class ProductionShapedNativeTargetOperations : INativeVerifiedComposerTargetOperations
        {
            private static readonly INativeVerifiedComposerTarget Target = new ProductionShapedNativeTarget();
            private readonly ProductionShapedWindowsScenario _scenario;

            public ProductionShapedNativeTargetOperations(ProductionShapedWindowsScenario scenario)
            {
                _scenario = scenario;
            }

            public INativeVerifiedComposerTarget? Reacquire(TextSurfaceDescriptor surface) =>
                _scenario.TryReacquire(surface) ? Target : null;

            public NativeComposerTextReadAttempt ReadText(
                INativeVerifiedComposerTarget target,
                TextSurfaceDescriptor surface)
            {
                _scenario.NativeOperationInvocationCount++;
                _scenario.ObservedOperations.Add("read");
                var text = _scenario.ReturnWrongTextAfterWrite && _scenario.WriteCount > 0
                    ? "unexpected"
                    : _scenario.CurrentText;
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
