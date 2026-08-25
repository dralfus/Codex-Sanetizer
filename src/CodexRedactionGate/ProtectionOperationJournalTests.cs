using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using NUnit.Framework;

namespace CodexRedactionGate;

[TestFixture]
public sealed class ProtectionOperationJournalTests
{
    [Test]
    public void Append_PersistsOrderedRawFreeEventsThatCanBeRenderedInStatus()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-operation-log-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new ProtectionOperationJournal(
                DefaultStorageLayout.Create(directory),
                () => new DateTimeOffset(2026, 8, 24, 8, 30, 0, TimeSpan.Zero));

            journal.Append("tray", "open_status", "intent_received", "running", "none");
            journal.Append("setup", "focused_setup", "waiting_for_focus", "running", "focus_message_composer", 17);
            journal.Append("setup", "focused_setup", "verification_failed", "failed", "fingerprint_incomplete", 17);

            var events = journal.ReadRecent(10);

            Assert.That(events.Select(item => item.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
            Assert.That(events[^1].ResultCode, Is.EqualTo("fingerprint_incomplete"));
            Assert.That(events[^1].AttemptId, Is.EqualTo(17));
            Assert.That(File.ReadAllText(journal.Path), Does.Not.Contain("test.secret.com"));
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
    public void StatusView_ShowsRecentOperationStagesAndTerminalResult()
    {
        var events = new[]
        {
            new ProtectionOperationEvent(1, DateTimeOffset.UtcNow, "setup", "focused_setup", "waiting_for_focus", "running", "focus_message_composer", 21),
            new ProtectionOperationEvent(2, DateTimeOffset.UtcNow, "setup", "focused_setup", "verification_failed", "failed", "surface_unverified", 21)
        };

        var state = new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: "idle",
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: null,
            LastApplied: false,
            LastSubmitted: false);
        var view = LocalProtectionStatusView.Create(state, events);
        var activity = view.Rows.Single(row => row.Name == "Recent protection activity");

        Assert.That(activity.OperationalState, Is.EqualTo("failed"));
        Assert.That(activity.Consequence, Does.Contain("waiting_for_focus"));
        Assert.That(activity.Consequence, Does.Contain("surface_unverified"));
    }

    [Test]
    [Apartment(ApartmentState.STA)]
    public void SetupWindow_ShowsCountdownStageActionAndPendingResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-setup-ui-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var form = new FirstRunSetupForm(
                DefaultStorageLayout.Create(directory),
                new SetupStatusController());
            form.Show();

            form.StartVerificationCountdownForAcceptance("Ctrl+Enter");

            Assert.That(form.VerificationStatusText, Does.Contain("Stage: waiting for focus"));
            Assert.That(form.VerificationStatusText, Does.Contain("Action: click inside"));
            Assert.That(form.VerificationStatusText, Does.Contain("Result: pending"));
            Assert.That(form.VerificationStatusText, Does.Contain("10 seconds remaining"));
            Assert.That(form.VerificationProgressMaximum, Is.EqualTo(10));
            Assert.That(form.VerificationProgressValue, Is.EqualTo(0));
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
    [Apartment(ApartmentState.STA)]
    public void SetupWindow_KeepsVerificationAndExitButtonsVisibleInsideClientArea()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-setup-layout-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var form = new FirstRunSetupForm(
                DefaultStorageLayout.Create(directory),
                new SetupStatusController());
            form.Show();
            Application.DoEvents();

            var buttons = Descendants(form)
                .OfType<Button>()
                .Where(button => button.Text is "Verify active app" or "Exit setup")
                .ToArray();

            Assert.That(buttons.Select(button => button.Text),
                Is.EquivalentTo(new[] { "Verify active app", "Exit setup" }));
            foreach (var button in buttons)
            {
                var boundsInForm = form.RectangleToClient(button.Parent!.RectangleToScreen(button.Bounds));
                Assert.That(button.Visible, Is.True, $"{button.Text} must be visible");
                Assert.That(boundsInForm.Height, Is.GreaterThan(0), $"{button.Text} must have height");
                Assert.That(boundsInForm.Bottom, Is.LessThanOrEqualTo(form.ClientSize.Height),
                    $"{button.Text} must stay inside the setup window");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class SetupStatusController : IFirstRunSetupController
    {
        public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout) => GetSetupStatus(layout);

        public FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout) => GetSetupStatus(layout, profileId);

        public bool IsSetupComplete(DefaultStorageLayout layout) => false;

        public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null) =>
            new(
                false,
                "setup_required",
                new FirstRunSetupState(true, new[] { "focused_supported_app" }, "pending", false, false),
                new Dictionary<string, string>());
    }

}
