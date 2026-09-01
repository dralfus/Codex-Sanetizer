using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class ProtectedSendTransactionTests
{
    private const string RawMarker = "RAW_SECRET_7C4F";

    [Test]
    public void LegacyPipelineHost_ExposesStageAndSideEffectOwnershipToItsCaller()
    {
        var methods = typeof(IProtectedSendPipelineHost).GetMethods().Select(method => method.Name).ToArray();
        Assert.That(methods, Does.Contain("PublishProtectedSendTrace"));
        Assert.That(methods, Does.Contain("AcquireProtectedSendSideEffect"));
    }

    [Test]
    public void Execute_SafePrompt_WritesVerifiesReplaysAndPublishesOneRawFreeSuccess()
    {
        var fixture = new TransactionFixture(RawMarker, text => new SanitizedPrompt(text, false));
        var result = fixture.Execute();
        Assert.That(result.Outcome, Is.EqualTo(ProtectedSendTerminalOutcome.SentSafely));
        Assert.That(result.TerminalPublished, Is.True);
        Assert.That(fixture.Session.Writes, Is.EqualTo(new[] { RawMarker }));
        Assert.That(fixture.Session.Replays, Is.EqualTo(1));
        Assert.That(fixture.Terminals, Has.Count.EqualTo(1));
        AssertRawFree(result, fixture);
    }

    [Test]
    public void Execute_SensitiveEdit_ResanitizesThenConfirmsBeforeWrite()
    {
        var fixture = new TransactionFixture(RawMarker, text => new SanitizedPrompt($"safe:{text}", true));
        fixture.Environment.Decision = SanitizedPromptDecision.Edit("edited");
        var result = fixture.Execute();
        Assert.That(result.Outcome, Is.EqualTo(ProtectedSendTerminalOutcome.SentSafely));
        Assert.That(fixture.Environment.SanitizedInputs, Is.EqualTo(new[] { RawMarker, "edited" }));
        Assert.That(fixture.Session.Writes, Is.EqualTo(new[] { "safe:edited" }));
    }

    [Test]
    public void Execute_CancelledConfirmation_PublishesOneCancelledTerminalWithoutSideEffect()
    {
        var fixture = new TransactionFixture(RawMarker, text => new SanitizedPrompt("safe", true));
        fixture.Environment.Decision = SanitizedPromptDecision.Cancel();
        var result = fixture.Execute();
        Assert.That(result.Outcome, Is.EqualTo(ProtectedSendTerminalOutcome.Cancelled));
        Assert.That(fixture.Session.Writes, Is.Empty);
        Assert.That(fixture.Session.Replays, Is.Zero);
        Assert.That(fixture.Terminals, Has.Count.EqualTo(1));
        AssertRawFree(result, fixture);
    }

    [TestCase("TargetChanged")]
    [TestCase("StaleGeneration")]
    [TestCase("WriteMismatch")]
    [TestCase("ReplayUncertain")]
    [TestCase("ContinuationDenied")]
    public void Execute_FailClosedConditions_BlockWithoutSuccess(string failureName)
    {
        var failure = Enum.Parse<TransactionFailure>(failureName);
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Session.Failure = failure;
        if (failure == TransactionFailure.StaleGeneration) fixture.Environment.GenerationCurrent = false;
        if (failure == TransactionFailure.ContinuationDenied) fixture.Environment.ContinuationAllowed = false;
        var result = fixture.Execute();
        Assert.That(result.Outcome, Is.EqualTo(ProtectedSendTerminalOutcome.Blocked));
        Assert.That(result.Reason, Is.EqualTo(failure.ToString()));
        Assert.That(fixture.Session.Replays, Is.EqualTo(failure == TransactionFailure.ReplayUncertain ? 1 : 0));
        Assert.That(fixture.Terminals, Has.Count.EqualTo(1));
    }

    [Test]
    public void Execute_DuplicateAdmission_ObservesCommittedTerminalWithoutSecondPublicationOrReplay()
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        var first = fixture.Execute();
        var second = fixture.Execute();
        Assert.That(second, Is.EqualTo(first));
        Assert.That(fixture.Session.Replays, Is.EqualTo(1));
        Assert.That(fixture.Terminals, Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Execute_TerminalPublicationDoesNotCommit_FailsClosedWithoutRetry(bool throws)
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Environment.TerminalThrows = throws;
        fixture.Environment.TerminalSucceeds = false;
        var result = fixture.Execute();
        Assert.That(result.Outcome, Is.EqualTo(ProtectedSendTerminalOutcome.Blocked));
        Assert.That(result.Reason, Is.EqualTo("terminal_publication_failed"));
        Assert.That(result.TerminalPublished, Is.False);
        Assert.That(fixture.Environment.TerminalAttempts, Is.EqualTo(1));
        Assert.That(fixture.Session.Replays, Is.EqualTo(1));
    }

    [Test]
    public void Execute_ForeignFactoryTarget_BlocksBeforeReadWriteOrReplay()
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Session.TargetIdentity = "foreign-target";
        var result = fixture.Execute();
        Assert.That(result.Reason, Is.EqualTo(nameof(TransactionFailure.TargetChanged)));
        Assert.That(fixture.Session.Reads, Is.Zero);
        Assert.That(fixture.Session.Writes, Is.Empty);
        Assert.That(fixture.Session.Replays, Is.Zero);
        Assert.That(fixture.Environment.FactoryRequests, Is.EqualTo(new[] { fixture.Request.TargetIdentity }));
    }

    [Test]
    public void Execute_RevalidatedChangedTarget_BlocksBeforeWriteOrReplay()
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Session.RevalidatedTargetIdentity = "changed-target";
        var result = fixture.Execute();
        Assert.That(result.Reason, Is.EqualTo(nameof(TransactionFailure.TargetChanged)));
        Assert.That(fixture.Session.Writes, Is.Empty);
        Assert.That(fixture.Session.Replays, Is.Zero);
    }

    [Test]
    public void Execute_LeaseUnavailable_BlocksBeforeWriteOrReplay()
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Environment.LeaseAvailable = false;
        var result = fixture.Execute();
        Assert.That(result.Reason, Is.EqualTo("lease_unavailable"));
        Assert.That(fixture.Session.Writes, Is.Empty);
        Assert.That(fixture.Session.Replays, Is.Zero);
    }

    [Test]
    public void Execute_CancellationRaceAfterWrite_BlocksBeforeReplay()
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Session.AfterWrite = () => fixture.Environment.ContinuationAllowed = false;
        var result = fixture.Execute();
        Assert.That(result.Reason, Is.EqualTo(nameof(TransactionFailure.ContinuationDenied)));
        Assert.That(fixture.Session.Writes, Is.EqualTo(new[] { "safe" }));
        Assert.That(fixture.Session.Replays, Is.Zero);
    }

    [Test]
    public void Execute_TraceFailureAfterReplay_BlocksWithoutSecondReplayAndPublishesOnce()
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Environment.FailTraceAt = ProtectedSendTransactionStage.Terminal;
        var result = fixture.Execute();
        Assert.That(result.Outcome, Is.EqualTo(ProtectedSendTerminalOutcome.Blocked));
        Assert.That(result.Reason, Is.EqualTo("trace_failure"));
        Assert.That(fixture.Session.Replays, Is.EqualTo(1));
        Assert.That(fixture.Terminals, Has.Count.EqualTo(1));
    }

    [Test]
    public void Execute_TraceException_IsConvertedToOneBlockedRawFreeTerminal()
    {
        var fixture = new TransactionFixture("prompt", text => new SanitizedPrompt("safe", false));
        fixture.Environment.ThrowTraceAt = ProtectedSendTransactionStage.Sanitized;
        var result = fixture.Execute();
        Assert.That(result.Reason, Is.EqualTo("trace_failure"));
        Assert.That(fixture.Terminals, Has.Count.EqualTo(1));
    }

    private static void AssertRawFree(ProtectedSendTerminalResult result, TransactionFixture fixture)
    {
        var evidence = string.Join("|", new[] { result.ToString() }.Concat(fixture.Environment.Trace.Select(trace => trace.ToString())).Concat(fixture.Terminals.Select(terminal => terminal.ToString())));
        Assert.That(evidence, Does.Not.Contain(RawMarker));
    }

    private sealed class TransactionFixture
    {
        internal TransactionFixture(string prompt, Func<string, SanitizedPrompt> sanitize)
        {
            Request = new AdmittedProtectedSend(7, "opaque-target");
            Session = new FakeSession(prompt, Request.TargetIdentity);
            Environment = new DeterministicTransactionEnvironment(Session, sanitize);
            Transaction = new ProtectedSendTransaction(Environment);
        }
        internal AdmittedProtectedSend Request { get; }
        internal FakeSession Session { get; }
        internal DeterministicTransactionEnvironment Environment { get; }
        internal ProtectedSendTransaction Transaction { get; }
        internal List<ProtectedSendTerminalEvidence> Terminals => Environment.Terminals;
        internal ProtectedSendTerminalResult Execute() => Transaction.Execute(Request);
    }

    private sealed class DeterministicTransactionEnvironment : IProtectedSendTransactionEnvironment
    {
        private readonly FakeSession _session;
        private readonly Func<string, SanitizedPrompt> _sanitize;
        private int _confirmationCalls;
        internal DeterministicTransactionEnvironment(FakeSession session, Func<string, SanitizedPrompt> sanitize) { _session = session; _sanitize = sanitize; }
        internal bool GenerationCurrent { get; set; } = true;
        internal bool ContinuationAllowed { get; set; } = true;
        internal bool LeaseAvailable { get; set; } = true;
        internal bool TerminalSucceeds { get; set; } = true;
        internal bool TerminalThrows { get; set; }
        internal int TerminalAttempts { get; private set; }
        internal SanitizedPromptDecision? Decision { get; set; }
        internal ProtectedSendTransactionStage? FailTraceAt { get; set; }
        internal ProtectedSendTransactionStage? ThrowTraceAt { get; set; }
        internal List<string> SanitizedInputs { get; } = new();
        internal List<string> FactoryRequests { get; } = new();
        internal List<ProtectedSendTraceEvidence> Trace { get; } = new();
        internal List<ProtectedSendTerminalEvidence> Terminals { get; } = new();
        public IProtectedSendTransactionSession CreateSession(AdmittedProtectedSend request) { FactoryRequests.Add(request.TargetIdentity); return _session; }
        public SanitizedPrompt Sanitize(string prompt) { SanitizedInputs.Add(prompt); return _sanitize(prompt); }
        public SanitizedPromptDecision Confirm(SanitizedPrompt prompt) => Decision is { Kind: SanitizedPromptDecisionKind.Edit } && _confirmationCalls++ == 0 ? Decision : Decision is { Kind: SanitizedPromptDecisionKind.Edit } ? SanitizedPromptDecision.Confirm() : Decision ?? SanitizedPromptDecision.Confirm();
        public bool IsGenerationCurrent(AdmittedProtectedSend request) => GenerationCurrent;
        public bool CanContinue(AdmittedProtectedSend request) => ContinuationAllowed;
        public IDisposable? AcquireLease(AdmittedProtectedSend request) => LeaseAvailable ? new Lease() : null;
        public bool PublishTrace(ProtectedSendTraceEvidence trace) { if (trace.Stage == ThrowTraceAt) throw new InvalidOperationException(); Trace.Add(trace); return trace.Stage != FailTraceAt; }
        public bool PublishTerminal(ProtectedSendTerminalEvidence terminal) { TerminalAttempts++; Terminals.Add(terminal); if (TerminalThrows) throw new InvalidOperationException(); return TerminalSucceeds; }
    }

    private sealed class FakeSession : IProtectedSendTransactionSession
    {
        private readonly string _prompt;
        internal FakeSession(string prompt, string targetIdentity) { _prompt = prompt; TargetIdentity = targetIdentity; RevalidatedTargetIdentity = targetIdentity; }
        public string TargetIdentity { get; set; }
        internal string RevalidatedTargetIdentity { get; set; }
        internal TransactionFailure? Failure { get; set; }
        internal Action? AfterWrite { get; set; }
        internal int Reads { get; private set; }
        internal List<string> Writes { get; } = new();
        internal int Replays { get; private set; }
        public TransactionStageResult Read() { Reads++; return ResultFor(TransactionFailure.ReadFailed, _prompt); }
        public TransactionStageResult Revalidate() => ResultFor(TransactionFailure.TargetChanged, targetIdentity: RevalidatedTargetIdentity);
        public TransactionStageResult WriteAndVerify(string text) { Writes.Add(text); AfterWrite?.Invoke(); return ResultFor(TransactionFailure.WriteMismatch); }
        public TransactionStageResult Replay() { Replays++; return ResultFor(TransactionFailure.ReplayFailed, replay: true); }
        private TransactionStageResult ResultFor(TransactionFailure failure, string? text = null, string? targetIdentity = null, bool replay = false)
        {
            var active = Failure == failure || (replay && Failure == TransactionFailure.ReplayUncertain);
            return active ? TransactionStageResult.Failed(replay && Failure == TransactionFailure.ReplayUncertain ? Failure.Value : failure) : TransactionStageResult.Success(text, targetIdentity);
        }
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }
}
