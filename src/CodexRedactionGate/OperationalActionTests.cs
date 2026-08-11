using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class OperationalActionTests
{
    [Test]
    public void LifecyclePublishesStagesAndPersistsOneRawFreeTerminalRecord()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var now = 1000L;
            var lifecycle = new ResidentOperationalActionLifecycle(
                layout,
                () => now,
                new OperationalActionJournal(layout, maxRecords: 20));

            var started = lifecycle.Start(
                "local_readiness",
                "prepare_storage",
                userInputRequired: false,
                nextAction: "wait_for_result");
            Assert.That(started.Started, Is.True);
            Assert.That(lifecycle.State.Status, Is.EqualTo("running"));
            Assert.That(lifecycle.State.Stage, Is.EqualTo("prepare_storage"));

            now = 1250L;
            Assert.That(lifecycle.PublishStage("verify_local_protection", userInputRequired: false, "wait_for_result", started.AttemptId), Is.True);
            Assert.That(lifecycle.State.ElapsedMilliseconds, Is.EqualTo(250));

            now = 1400L;
            Assert.That(lifecycle.Complete("succeeded", "none", started.AttemptId), Is.True);
            Assert.That(lifecycle.State.Status, Is.EqualTo("succeeded"));
            Assert.That(lifecycle.State.CanCancel, Is.False);

            var journal = OperationalActionJournal.Read(layout);
            Assert.That(journal, Has.Count.EqualTo(3));
            Assert.That(journal[^1].Transition, Is.EqualTo("completed"));
            Assert.That(journal[^1].OutcomeCode, Is.EqualTo("succeeded"));
            Assert.That(journal.SelectMany(entry => new[]
            {
                entry.CorrelationId,
                entry.ActionKind,
                entry.Transition,
                entry.Stage,
                entry.OutcomeCode,
                entry.BuildVersion
            }), Has.None.Contains("DOMAIN_C195C3D8E8F3"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void CancelIsTerminalAndRetryStartsANewCorrelatedAttempt()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var lifecycle = new ResidentOperationalActionLifecycle(layout, () => 1000L);

            var first = lifecycle.Start("first_run_setup", "waiting_for_focus", userInputRequired: true, "focus_message_composer");
            Assert.That(lifecycle.Cancel("user_cancelled", first.AttemptId), Is.True);
            Assert.That(lifecycle.State.Status, Is.EqualTo("cancelled"));
            Assert.That(lifecycle.State.OutcomeCode, Is.EqualTo("user_cancelled"));

            var second = lifecycle.Start("first_run_setup", "waiting_for_focus", userInputRequired: true, "focus_message_composer");
            Assert.That(second.Started, Is.True);
            Assert.That(second.AttemptId, Is.Not.EqualTo(first.AttemptId));
            Assert.That(second.CorrelationId, Is.Not.EqualTo(first.CorrelationId));
            Assert.That(lifecycle.State.Status, Is.EqualTo("running"));
            Assert.That(lifecycle.State.CanCancel, Is.True);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LifecycleRejectsConcurrentStartAndInvalidTerminalTransition()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var lifecycle = new ResidentOperationalActionLifecycle(layout, () => 1000L);

            Assert.That(lifecycle.Start("local_readiness", "prepare_storage", false, "wait_for_result").Started, Is.True);
            Assert.That(lifecycle.Start("other_action", "prepare_storage", false, "wait_for_result").Started, Is.False);
            Assert.That(lifecycle.Complete("bad code", "none", lifecycle.State.AttemptId), Is.False);
            Assert.That(lifecycle.State.Status, Is.EqualTo("running"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LifecycleRejectsAnUncorrelatedTerminalCompletion()
    {
        var directory = CreateTempDirectory();
        try
        {
            var lifecycle = new ResidentOperationalActionLifecycle(DefaultStorageLayout.Create(directory), () => 1000L);
            var started = lifecycle.Start("local_readiness", "starting", false, "wait_for_result");

            Assert.That(started.Started, Is.True);
            Assert.That(lifecycle.Complete("succeeded", "none"), Is.False);
            Assert.That(lifecycle.State.Status, Is.EqualTo("running"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void OperationalJournalRejectsDomainAndAddressLikeLifecycleValues()
    {
        var directory = CreateTempDirectory();
        try
        {
            var journal = new OperationalActionJournal(DefaultStorageLayout.Create(directory));
            var validCorrelation = Guid.NewGuid().ToString("N");

            Assert.That(journal.TryAppend(new OperationalActionJournalEntry(
                validCorrelation,
                "local_readiness",
                "completed",
                "check_local_prerequisites",
                "succeeded",
                1,
                1,
                BuildVersion.Current)), Is.True);
            Assert.That(journal.TryAppend(new OperationalActionJournalEntry(
                Guid.NewGuid().ToString("N"),
                "local_readiness",
                "completed",
                "test.secret.com",
                "succeeded",
                1,
                2,
                BuildVersion.Current)), Is.False);
            Assert.That(journal.TryAppend(new OperationalActionJournalEntry(
                Guid.NewGuid().ToString("N"),
                "local_readiness",
                "completed",
                "check_local_prerequisites",
                "192.168.10.25",
                1,
                3,
                BuildVersion.Current)), Is.False);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void NonSuccessCompletionIsPublishedAsFailed()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var lifecycle = new ResidentOperationalActionLifecycle(layout, () => 1000L);

            Assert.That(lifecycle.Start("first_run_setup", "starting", false, "retry_setup").Started, Is.True);
            var started = lifecycle.State.AttemptId;
            Assert.That(lifecycle.Complete("setup_failed", "retry_setup", started), Is.True);
            Assert.That(lifecycle.State.Status, Is.EqualTo("failed"));
            Assert.That(lifecycle.State.CanCancel, Is.False);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void LocalReadinessWorkflowReturnsRawFreeTerminalResult()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var result = LocalReadinessWorkflow.Run(
                layout,
                () => new ReadinessReport(
                    Ready: false,
                    Items: new[] { new ReadinessItem("storage", "failed", "storage_unavailable") }));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("local_readiness_failed"));
            Assert.That(result.Items.Single().Code, Is.EqualTo("storage_unavailable"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void StaleWorkerCannotCompleteANewAttempt()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var lifecycle = new ResidentOperationalActionLifecycle(layout, () => 1000L);
            var first = lifecycle.Start("local_readiness", "starting", false, "wait_for_result");
            Assert.That(lifecycle.Cancel("cancelled", first.AttemptId), Is.True);
            var second = lifecycle.Start("local_readiness", "starting", false, "wait_for_result");

            Assert.That(lifecycle.Complete("succeeded", "none", first.AttemptId), Is.False);
            Assert.That(lifecycle.State.Status, Is.EqualTo("running"));
            Assert.That(lifecycle.State.AttemptId, Is.EqualTo(second.AttemptId));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void StatusProjectionShowsStageInputElapsedNextActionAndCancel()
    {
        var state = new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: "enabled",
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: "chatgpt-desktop",
            LastApplied: false,
            LastSubmitted: false,
            OperationalAction: new OperationalActionState(
                ActionKind: "local_readiness",
                Status: "running",
                Stage: "check_local_prerequisites",
                InputMode: "automatic",
                OutcomeCode: "none",
                NextAction: "wait_for_result",
                CanCancel: true,
                AttemptId: 2,
                CorrelationId: "0123456789abcdef0123456789abcdef",
                ElapsedMilliseconds: 250));

        var view = LocalProtectionStatusView.Create(state);
        var row = view.Rows.Single(item => item.Name == "Current automatic action");
        Assert.That(row.OperationalState, Is.EqualTo("running: check_local_prerequisites"));
        Assert.That(row.Consequence, Does.Contain("input: automatic"));
        Assert.That(row.Consequence, Does.Contain("elapsed: 250 ms"));
        Assert.That(row.Consequence, Does.Contain("next: wait_for_result"));
        Assert.That(row.Action, Is.EqualTo(LocalProtectionStatusAction.CancelOperationalAction));
    }

    [Test]
    public void LocalReadinessAndReleaseEvidenceRemainSeparateInStatus()
    {
        var state = new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: "enabled",
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: "chatgpt-desktop",
            LastApplied: false,
            LastSubmitted: false,
            ComposerProtected: false,
            ConfiguredProfileId: "chatgpt-desktop",
            ProtectedClaimStatus: "degraded",
            ReferenceAcceptanceStatus: "missing",
            LiveContractStatus: "missing",
            LocalReadinessStatus: "passed");

        var rows = LocalProtectionStatusView.Create(state).Rows;
        var readiness = rows.Single(item => item.Name == "Automatic local readiness");
        Assert.That(readiness.OperationalState, Is.EqualTo("completed"));
        Assert.That(readiness.Consequence, Does.Not.Contain("release/CI"));
    }

    [Test]
    public void ManualAcceptanceGateRemainsClosedWithoutResidentProof()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);

            var result = ManualAcceptanceGate.Evaluate(layout);

            Assert.That(result.Allowed, Is.False);
            Assert.That(result.Code, Is.EqualTo("resident_readiness_proof_missing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ManualAcceptanceGateOpensOnlyForCurrentSuccessfulResidentReadiness()
    {
        var directory = CreateTempDirectory();
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var lifecycle = new ResidentOperationalActionLifecycle(layout, () => 1000L);
            var started = lifecycle.Start(
                "local_readiness",
                "starting",
                userInputRequired: false,
                nextAction: "wait_for_result");

            Assert.That(started.Started, Is.True);
            Assert.That(lifecycle.Complete("succeeded", "none", started.AttemptId), Is.True);

            var result = ManualAcceptanceGate.Evaluate(layout);

            Assert.That(result.Allowed, Is.False);
            Assert.That(result.Code, Is.EqualTo("resident_readiness_proof_missing"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-operational-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
