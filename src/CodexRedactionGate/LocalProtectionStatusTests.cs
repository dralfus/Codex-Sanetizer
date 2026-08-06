using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

public sealed class LocalProtectionStatusTests
{
    [Test]
    [Apartment(System.Threading.ApartmentState.STA)]
    public void StatusForm_RefreshesExplicitlyWithoutLeakingRawValuesOrRequiringTheTimer()
    {
        var state = ProtectedTrayState();
        using var form = new LocalProtectionStatusForm(
            () => LocalProtectionStatusView.Create(state),
            _ => { });

        form.RefreshView();
        state = state with { NativeSubmitEnabled = false, ComposerProtected = false, LastStatus = "test.secret.com" };
        form.RefreshView();

        Assert.That(form.CurrentRows[1].OperationalState, Is.EqualTo("degraded"));
        Assert.That(string.Join(Environment.NewLine, form.CurrentRows), Does.Not.Contain("test.secret.com"));

        state = state with
        {
            NativeSubmitEnabled = true,
            ComposerProtected = true,
            LocalProtectionStatus = LocalProtectionRecovery.RecoveryRequiredCode
        };
        form.RefreshView();

        Assert.That(form.CurrentRows[0].OperationalState, Is.EqualTo("recovery required"));
        Assert.That(form.CurrentRows[1].OperationalState, Is.EqualTo("unavailable"));
        Assert.That(form.CurrentRows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RepairLocalProtection));

        state = state with { LocalProtectionStatus = LocalProtectionRecovery.ReadyCode };
        form.RefreshView();

        Assert.That(form.CurrentRows[0].OperationalState, Is.EqualTo("ready"));
        Assert.That(form.IsDisposed, Is.False);
    }

    [Test]
    [Apartment(System.Threading.ApartmentState.STA)]
    public void StatusForm_RendersSelectableReadOnlyStatusText()
    {
        using var form = new LocalProtectionStatusForm(
            () => LocalProtectionStatusView.Create(ProtectedTrayState()),
            _ => { });

        form.RefreshView();
        var textBoxes = form.RowControls
            .SelectMany(row => row.Controls.Cast<System.Windows.Forms.Control>())
            .OfType<System.Windows.Forms.TextBox>()
            .ToArray();

        Assert.That(textBoxes, Is.Not.Empty);
        Assert.That(textBoxes.All(text => text.ReadOnly && text.ShortcutsEnabled && text.TabStop), Is.True);
    }

    [Test]
    [Apartment(System.Threading.ApartmentState.STA)]
    public void StatusForm_DoesNotRefreshAwaySelectedStatusText()
    {
        var state = ProtectedTrayState();
        using var form = new LocalProtectionStatusForm(
            () => LocalProtectionStatusView.Create(state),
            _ => { });

        form.RefreshView();
        var text = form.RowControls
            .SelectMany(row => row.Controls.Cast<System.Windows.Forms.Control>())
            .OfType<System.Windows.Forms.TextBox>()
            .First();
        text.Select(0, 1);

        state = state with { NativeSubmitEnabled = false, ComposerProtected = false };
        form.RefreshView();

        Assert.That(form.CurrentRows[1].OperationalState, Is.EqualTo("active"));
        Assert.That(text.IsDisposed, Is.False);
        Assert.That(text.SelectionLength, Is.EqualTo(1));
    }

    [Test]
    public void StatusView_SeparatesReadyDpapiActivePromptAndBrokerOnlyFiles()
    {
        var view = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            ProjectFileStatus = ProjectFileProtectionStatusValues.BrokerDemoOnly
        });

        Assert.That(view.Rows, Has.Count.EqualTo(3));
        Assert.That(view.Rows[0], Is.EqualTo(new LocalProtectionStatusRow(
            "Local DPAPI protection",
            "DPAPI-backed local storage",
            "ready",
            "Local mappings are available to this Windows user.",
            LocalProtectionStatusAction.None)));
        Assert.That(view.Rows[1].OperationalState, Is.EqualTo("active"));
        Assert.That(view.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.None));
        Assert.That(view.Rows[2].OperationalState, Is.EqualTo("broker demo only"));
        Assert.That(view.Rows[2].Consequence, Does.Contain("not intercepted"));
        Assert.That(view.Rows[2].Consequence, Does.Contain("unsupported"));
    }

    [Test]
    public void StatusView_RendersRawFreeProtectedSendAttemptFromResidentState()
    {
        var checking = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            ProtectedSendAttemptStatus = "checking",
            ProtectedSendAttemptAction = "checking_prompt",
            LastStatus = "DOMAIN_C195C3D8E8F3"
        });
        var sent = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            ProtectedSendAttemptStatus = "sent_safely",
            ProtectedSendAttemptAction = "none"
        });
        var canceled = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            ProtectedSendAttemptStatus = "canceled",
            ProtectedSendAttemptAction = "edit_or_send_again"
        });

        Assert.That(checking.Rows[1].OperationalState, Is.EqualTo("checking Send"));
        Assert.That(sent.Rows[1].OperationalState, Is.EqualTo("last Send protected"));
        Assert.That(canceled.Rows[1].OperationalState, Is.EqualTo("last Send canceled"));
        Assert.That(new[] { checking, sent, canceled }
            .All(view => !view.RenderText().Contains("DOMAIN_C195C3D8E8F3", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void StatusView_RendersInterruptedSendOutcomeSeparatelyFromCurrentAttempt()
    {
        var view = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            ProtectedSendAttemptStatus = "idle",
            LastProtectedSendInterruption = new ProtectedSendInterruption(
                AttemptId: 12,
                SourceGeneration: 7,
                Reason: "runtime_replaced",
                Action: "retry_protection")
        });

        Assert.That(view.Rows[1].OperationalState, Is.EqualTo("previous Send interrupted"));
        Assert.That(view.Rows[1].Consequence, Does.Contain("Retry prompt protection"));
        Assert.That(view.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RetryPromptProtection));
    }

    [Test]
    public void StatusView_PrioritizesInterruptedSendOutcomeOverSetupOrRecoveryState()
    {
        var interruption = new ProtectedSendInterruption(
            AttemptId: 12,
            SourceGeneration: 7,
            Reason: "runtime_replaced",
            Action: "retry_protection");

        foreach (var state in new[]
        {
            ProtectedTrayState() with
            {
                SetupRequired = true,
                LastProtectedSendInterruption = interruption
            },
            ProtectedTrayState() with
            {
                LocalProtectionStatus = LocalProtectionRecovery.RecoveryRequiredCode,
                LastProtectedSendInterruption = interruption
            }
        })
        {
            var view = LocalProtectionStatusView.Create(state);

            Assert.That(view.Rows[1].OperationalState, Is.EqualTo("previous Send interrupted"));
            Assert.That(view.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RetryPromptProtection));
        }
    }

    [Test]
    public void StatusView_ExplainsEveryBlockedProtectedSendAttemptWithoutRawValues()
    {
        foreach (var (attemptStatus, expectedState, expectedAction, expectedConsequence) in new[]
        {
            ("composer_changed", "Send blocked", LocalProtectionStatusAction.None, "Focus the original composer"),
            ("binding_not_verified", "Send blocked", LocalProtectionStatusAction.VerifyProfiles, "Verify prompt protection"),
            ("setup_required", "Send blocked", LocalProtectionStatusAction.VerifyProfiles, "Verify prompt protection"),
            ("local_protection_unavailable", "Send blocked: local protection unavailable", LocalProtectionStatusAction.RepairLocalProtection, "Repair local protection"),
            ("policy_blocked", "Send blocked by policy", LocalProtectionStatusAction.None, "contact the administrator"),
            ("protection_unavailable", "Send blocked: protection unavailable", LocalProtectionStatusAction.RetryPromptProtection, "Retry prompt protection"),
            ("content_blocked", "Send blocked by content policy", LocalProtectionStatusAction.None, "Edit the prompt")
        })
        {
            var view = LocalProtectionStatusView.Create(ProtectedTrayState() with
            {
                ProtectedSendAttemptStatus = attemptStatus,
                ProtectedSendAttemptAction = "DOMAIN_C195C3D8E8F3",
                LastStatus = "DOMAIN_C195C3D8E8F3"
            });
            var promptRow = view.Rows[1];

            Assert.That(promptRow.OperationalState, Is.EqualTo(expectedState), attemptStatus);
            Assert.That(promptRow.Action, Is.EqualTo(expectedAction), attemptStatus);
            Assert.That(promptRow.Consequence, Does.Contain(expectedConsequence), attemptStatus);
            Assert.That(view.RenderText(), Does.Not.Contain("DOMAIN_C195C3D8E8F3"), attemptStatus);
        }
    }

    [Test]
    public void StatusView_ExplainsSetupProgressAndSpecificBlockedSendReasons()
    {
        var waiting = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            SetupRequired = true,
            ComposerProtected = false,
            SetupVerificationStatus = "waiting_for_focus",
            SetupVerificationAction = "focus_message_composer",
            SetupVerificationBinding = "Ctrl+Enter"
        });
        var policyBlocked = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            ProtectedSendAttemptStatus = "policy_blocked",
            ProtectedSendAttemptAction = "contact_administrator"
        });

        Assert.That(waiting.Rows[1].OperationalState, Is.EqualTo("waiting for focus"));
        Assert.That(waiting.Rows[1].Consequence, Does.Contain("Focus the selected app composer"));
        Assert.That(policyBlocked.Rows[1].OperationalState, Is.EqualTo("Send blocked by policy"));
        Assert.That(policyBlocked.Rows[1].Consequence, Does.Contain("contact the administrator"));
    }

    [Test]
    public void StatusView_ExplainsRecoveryAndSetupWithoutClaimingProtection()
    {
        var state = ProtectedTrayState() with
        {
            NativeSubmitEnabled = false,
            NativeSubmitStatus = OsInteractionStatusIds.NativeSubmitSetupRequired,
            ComposerProtected = false,
            SetupRequired = true,
            LocalProtectionStatus = LocalProtectionRecovery.RecoveryRequiredCode
        };

        var view = LocalProtectionStatusView.Create(state);

        Assert.That(view.Rows[0].OperationalState, Is.EqualTo("recovery required"));
        Assert.That(view.Rows[0].Action, Is.EqualTo(LocalProtectionStatusAction.RepairLocalProtection));
        Assert.That(view.Rows[1].OperationalState, Is.EqualTo("unavailable"));
        Assert.That(view.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RepairLocalProtection));
        Assert.That(view.Rows[1].Consequence, Does.Contain("local protection is ready"));
        Assert.That(view.Rows[2].OperationalState, Is.EqualTo("not configured"));
    }

    [Test]
    public void StatusView_UsesVerificationOnlyForSetupAndRetryOnlyForDegradedProtection()
    {
        var setup = ProtectedTrayState() with
        {
            NativeSubmitEnabled = false,
            NativeSubmitStatus = OsInteractionStatusIds.NativeSubmitSetupRequired,
            ComposerProtected = false,
            SetupRequired = true
        };
        var degraded = ProtectedTrayState() with
        {
            NativeSubmitEnabled = false,
            ComposerProtected = false
        };

        var setupView = LocalProtectionStatusView.Create(setup);
        var degradedView = LocalProtectionStatusView.Create(degraded);

        Assert.That(setupView.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.VerifyProfiles));
        Assert.That(degradedView.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RetryPromptProtection));
    }

    [Test]
    public void StatusView_ShowsRawFreeFailureAfterProtectionRetryFails()
    {
        var failure = "DOMAIN_C195C3D8E8F3";
        var view = LocalProtectionStatusView.Create(
            ProtectedTrayState() with
            {
                NativeSubmitEnabled = false,
                ComposerProtected = false,
                LastStatus = failure,
                PromptProtectionRetryFailed = true
            });

        Assert.That(view.Rows[1].Consequence, Does.Contain("retry failed"));
        Assert.That(view.Rows[1].Consequence, Does.Contain("stays blocked"));
        Assert.That(view.RenderText(), Does.Not.Contain(failure));
    }

    [TestCase(LocalProtectionRecovery.RecoveryRequiredCode)]
    [TestCase("DOMAIN_C195C3D8E8F3")]
    public void StatusView_DoesNotClaimPromptProtectionActiveWhenLocalProtectionIsNotReady(string localProtectionStatus)
    {
        var view = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            LocalProtectionStatus = localProtectionStatus
        });

        Assert.That(view.Rows[0].OperationalState, Is.Not.EqualTo("ready"));
        Assert.That(view.Rows[1].OperationalState, Is.EqualTo("unavailable"));
        Assert.That(view.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RepairLocalProtection));
    }

    [Test]
    public void StatusView_RenderedTextIsRawFree()
    {
        var sensitivePath = "C:\\Users\\user1\\private\\.env";
        var sensitiveTerm = "test.secret.com";
        var view = LocalProtectionStatusView.Create(ProtectedTrayState() with
        {
            LastStatus = sensitiveTerm,
            LastProfileId = sensitivePath,
            ProjectFileStatus = ProjectFileProtectionStatusValues.UnprotectedNoBroker
        });
        var rendered = view.RenderText();

        Assert.That(rendered, Does.Not.Contain(sensitivePath));
        Assert.That(rendered, Does.Not.Contain(sensitiveTerm));
        Assert.That(rendered, Does.Contain("unsupported"));
    }

    [Test]
    public void ProjectFileStatusInspector_ReportsBrokerOnlyAfterLocalWorkspacePolicyChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-status-tests", System.Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(Path.Combine(directory, "data"));
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);

        try
        {
            Assert.That(ProjectFileProtectionStatusInspector.Inspect(layout), Is.EqualTo(ProjectFileProtectionStatusValues.NotConfigured));

            ProtectedWorkspaceStore.Protect(layout, workspace);

            Assert.That(ProjectFileProtectionStatusInspector.Inspect(layout), Is.EqualTo(ProjectFileProtectionStatusValues.BrokerDemoOnly));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void ProjectFileStatusInspector_ReportsUnsupportedWhenWorkspaceRegistryIsUnreadable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-status-tests", System.Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(Path.Combine(directory, "data"));
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);

        try
        {
            ProtectedWorkspaceStore.Protect(layout, workspace);
            File.WriteAllText(ProtectedWorkspaceStore.DefaultPath(layout), "{not-json");

            var status = ProjectFileProtectionStatusInspector.Inspect(layout);
            var view = LocalProtectionStatusView.Create(ProtectedTrayState() with
            {
                ProjectFileStatus = status
            });

            Assert.That(status, Is.EqualTo(ProjectFileProtectionStatusValues.UnprotectedNoBroker));
            Assert.That(view.Rows[2].OperationalState, Is.EqualTo("unsupported"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestCase(true, false, false, "degraded")]
    [TestCase(false, false, false, "disabled")]
    public void StatusView_ReportsNonActivePromptStatesTruthfully(
        bool enabled,
        bool nativeSubmitEnabled,
        bool composerProtected,
        string expectedStatus)
    {
        var state = ProtectedTrayState() with
        {
            Enabled = enabled,
            NativeSubmitEnabled = nativeSubmitEnabled,
            ComposerProtected = composerProtected,
            ProjectFileStatus = ProjectFileProtectionStatusValues.Protected
        };

        var view = LocalProtectionStatusView.Create(state);

        Assert.That(view.Rows[0].OperationalState, Is.EqualTo("ready"));
        Assert.That(view.Rows[1].OperationalState, Is.EqualTo(expectedStatus));
        Assert.That(view.Rows[2].OperationalState, Is.EqualTo("unsupported"));
    }

    [Test]
    public void StatusView_ReportsLiveProjectFileProtectionOnlyWhenTheLiveFlagIsTrue()
    {
        var liveState = ProtectedTrayState() with
        {
            ProjectFilesProtected = true,
            ProjectFileStatus = ProjectFileProtectionStatusValues.Protected
        };

        var view = LocalProtectionStatusView.Create(liveState);

        Assert.That(view.Rows[2].OperationalState, Is.EqualTo("live protected"));
        Assert.That(view.Rows[2].Consequence, Does.Contain("live ingress"));
    }

    [Test]
    public void StatusView_ReportsRecoveryRuntimeReplacementAsBlockedUntilTheReadyStateIsPublished()
    {
        var state = ProtectedTrayState() with
        {
            NativeSubmitEnabled = false,
            ComposerProtected = false,
            LastStatus = "test.secret.com",
            LocalProtectionStatus = "local_protection_reloading"
        };

        var replacing = LocalProtectionStatusView.Create(state);
        var ready = LocalProtectionStatusView.Create(ProtectedTrayState());

        Assert.That(replacing.Rows[0].OperationalState, Is.EqualTo("unavailable"));
        Assert.That(replacing.Rows[0].Consequence, Does.Contain("unavailable"));
        Assert.That(replacing.RenderText(), Does.Not.Contain("test.secret.com"));
        Assert.That(ready.Rows[0].OperationalState, Is.EqualTo("ready"));
    }

    [Test]
    [Apartment(System.Threading.ApartmentState.STA)]
    public void StatusForm_RefreshDisposesReplacedControlsAndCloseDisposesItsTimer()
    {
        var state = ProtectedTrayState();
        using var form = new LocalProtectionStatusForm(
            () => LocalProtectionStatusView.Create(state),
            _ => { });

        form.RefreshView();
        var replacedControls = form.RowControls.ToArray();
        state = state with { NativeSubmitEnabled = false, ComposerProtected = false };
        form.RefreshView();

        Assert.That(replacedControls, Is.Not.Empty);
        Assert.That(replacedControls.All(control => control.IsDisposed), Is.True);

        form.Close();
        Assert.That(form.IsRefreshTimerDisposed, Is.True);
    }

    [Test]
    [Apartment(System.Threading.ApartmentState.STA)]
    public void StatusView_RendersOnlyRawFreeTextFromSyntheticResidentState()
    {
        var rawValues = new[]
        {
            "C:\\Users\\user1\\private\\.env",
            "test.secret.com",
            "PROMPT_C195C3D8E8F3",
            "mapping-value",
            "exception-detail"
        };
        var state = ProtectedTrayState() with
        {
            LocalProtectionStatus = rawValues[0],
            ProjectFileStatus = rawValues[1],
            LastStatus = rawValues[2],
            LastProfileId = rawValues[3],
            ProtectedSendBinding = rawValues[4]
        };

        var view = LocalProtectionStatusView.Create(state);
        var rendered = view.RenderText();
        using var form = new LocalProtectionStatusForm(() => view, _ => { });
        form.RefreshView();
        var renderedControlText = string.Join(
            Environment.NewLine,
            form.RowControls.SelectMany(row => row.Controls.Cast<System.Windows.Forms.Control>()).Select(control => control.Text));

        Assert.That(rawValues.All(raw => !rendered.Contains(raw, StringComparison.Ordinal)), Is.True);
        Assert.That(rawValues.All(raw => !renderedControlText.Contains(raw, StringComparison.Ordinal)), Is.True);
        Assert.That(rendered, Does.Contain("unavailable"));
        Assert.That(rendered, Does.Contain("unsupported"));
    }

    [Test]
    public void TrayProtectionController_PublishesProjectFileStatusBeforeTheTrayRendersIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-status-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(Path.Combine(directory, "data"));
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);

        try
        {
            var controller = new TrayProtectionController(
                new SanitizerTests.FakeTrayHotkeyHost(),
                () => throw new AssertionException("Manual scan should not run."),
                nativeSubmitHookHost: null,
                nativeSubmitController: null,
                storageLayout: layout);
            Assert.That(controller.State.ProjectFileStatus, Is.EqualTo(ProjectFileProtectionStatusValues.NotConfigured));

            ProtectedWorkspaceStore.Protect(layout, workspace);
            controller.RefreshProjectFileProtectionStatus();

            Assert.That(controller.State.ProjectFileStatus, Is.EqualTo(ProjectFileProtectionStatusValues.BrokerDemoOnly));
            Assert.That(controller.State.ProjectFilesProtected, Is.False);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static TrayProtectionState ProtectedTrayState()
    {
        return new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: "Ctrl+Shift+F9",
            LastStatus: OsInteractionStatusIds.Protected,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: "chatgpt-desktop",
            LastApplied: false,
            LastSubmitted: false,
            NativeSubmitEnabled: true,
            NativeSubmitStatus: OsInteractionStatusIds.Protected,
            ProtectedSendBinding: "Enter",
            NewlineBinding: "Ctrl+Enter",
            ManualScanHotkey: "Ctrl+Shift+F9",
            ReadinessStatus: OsInteractionStatusIds.Protected,
            ComposerProtected: true,
            ProjectFilesProtected: false,
            ProjectFileStatus: ProjectFileProtectionStatusValues.NotConfigured,
            ResidentProcess: true);
    }
}
