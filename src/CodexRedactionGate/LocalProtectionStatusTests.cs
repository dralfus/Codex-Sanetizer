using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using CodexRedactionGate;

public sealed class LocalProtectionStatusTests
{
    [Test]
    [Apartment(System.Threading.ApartmentState.STA)]
    public void StatusForm_RefreshesExplicitlyWithoutLeakingRawValuesOrRequiringTheTimer()
    {
        var state = ProtectedTrayState();
        var localProtectionStatus = LocalProtectionRecovery.ReadyCode;
        using var form = new LocalProtectionStatusForm(
            () => LocalProtectionStatusView.Create(
                localProtectionStatus,
                state,
                ProjectFileProtectionStatusValues.NotConfigured),
            _ => { });

        form.RefreshView();
        state = state with { NativeSubmitEnabled = false, ComposerProtected = false, LastStatus = "test.secret.com" };
        form.RefreshView();

        Assert.That(form.CurrentRows[1].OperationalState, Is.EqualTo("degraded"));
        Assert.That(string.Join(Environment.NewLine, form.CurrentRows), Does.Not.Contain("test.secret.com"));

        state = state with { NativeSubmitEnabled = true, ComposerProtected = true };
        localProtectionStatus = LocalProtectionRecovery.RecoveryRequiredCode;
        form.RefreshView();

        Assert.That(form.CurrentRows[0].OperationalState, Is.EqualTo("recovery required"));
        Assert.That(form.CurrentRows[1].OperationalState, Is.EqualTo("active"));

        localProtectionStatus = LocalProtectionRecovery.ReadyCode;
        form.RefreshView();

        Assert.That(form.CurrentRows[0].OperationalState, Is.EqualTo("ready"));
        Assert.That(form.IsDisposed, Is.False);
    }

    [Test]
    public void StatusView_SeparatesReadyDpapiActivePromptAndBrokerOnlyFiles()
    {
        var view = LocalProtectionStatusView.Create(
            LocalProtectionRecovery.ReadyCode,
            ProtectedTrayState(),
            ProjectFileProtectionStatusValues.BrokerDemoOnly);

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
    }

    [Test]
    public void StatusView_ExplainsRecoveryAndSetupWithoutClaimingProtection()
    {
        var state = ProtectedTrayState() with
        {
            NativeSubmitEnabled = false,
            NativeSubmitStatus = OsInteractionStatusIds.NativeSubmitSetupRequired,
            ComposerProtected = false,
            SetupRequired = true
        };

        var view = LocalProtectionStatusView.Create(
            LocalProtectionRecovery.RecoveryRequiredCode,
            state,
            ProjectFileProtectionStatusValues.NotConfigured);

        Assert.That(view.Rows[0].OperationalState, Is.EqualTo("recovery required"));
        Assert.That(view.Rows[0].Action, Is.EqualTo(LocalProtectionStatusAction.RepairLocalProtection));
        Assert.That(view.Rows[1].OperationalState, Is.EqualTo("setup required"));
        Assert.That(view.Rows[1].Consequence, Does.Contain("blocked"));
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

        var setupView = LocalProtectionStatusView.Create(LocalProtectionRecovery.ReadyCode, setup, ProjectFileProtectionStatusValues.NotConfigured);
        var degradedView = LocalProtectionStatusView.Create(LocalProtectionRecovery.ReadyCode, degraded, ProjectFileProtectionStatusValues.NotConfigured);

        Assert.That(setupView.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.VerifyProfiles));
        Assert.That(degradedView.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RetryPromptProtection));
    }

    [Test]
    public void StatusView_ShowsRawFreeFailureAfterProtectionRetryFails()
    {
        var failure = "DOMAIN_C195C3D8E8F3";
        var view = LocalProtectionStatusView.Create(
            LocalProtectionRecovery.ReadyCode,
            ProtectedTrayState() with { NativeSubmitEnabled = false, ComposerProtected = false, LastStatus = failure },
            ProjectFileProtectionStatusValues.NotConfigured,
            promptProtectionRetryFailed: true);

        Assert.That(view.Rows[1].Consequence, Does.Contain("retry failed"));
        Assert.That(view.Rows[1].Consequence, Does.Contain("stays blocked"));
        Assert.That(view.RenderText(), Does.Not.Contain(failure));
    }

    [Test]
    public void StatusView_RenderedTextIsRawFree()
    {
        var sensitivePath = "C:\\Users\\user1\\private\\.env";
        var sensitiveTerm = "test.secret.com";
        var view = LocalProtectionStatusView.Create(
            LocalProtectionRecovery.ReadyCode,
            ProtectedTrayState() with { LastStatus = sensitiveTerm, LastProfileId = sensitivePath },
            ProjectFileProtectionStatusValues.UnprotectedNoBroker);
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
            ComposerProtected = composerProtected
        };

        var view = LocalProtectionStatusView.Create(
            "local_protection_unavailable",
            state,
            ProjectFileProtectionStatusValues.Protected);

        Assert.That(view.Rows[0].OperationalState, Is.EqualTo("unavailable"));
        Assert.That(view.Rows[1].OperationalState, Is.EqualTo(expectedStatus));
        Assert.That(view.Rows[2].OperationalState, Is.EqualTo("unsupported"));
    }

    [Test]
    public void StatusView_ReportsLiveProjectFileProtectionOnlyWhenTheLiveFlagIsTrue()
    {
        var liveState = ProtectedTrayState() with { ProjectFilesProtected = true };

        var view = LocalProtectionStatusView.Create(
            LocalProtectionRecovery.ReadyCode,
            liveState,
            ProjectFileProtectionStatusValues.Protected);

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
            LastStatus = "test.secret.com"
        };

        var replacing = LocalProtectionStatusView.Create(
            "local_protection_reloading",
            state,
            ProjectFileProtectionStatusValues.NotConfigured);
        var ready = LocalProtectionStatusView.Create(
            LocalProtectionRecovery.ReadyCode,
            ProtectedTrayState(),
            ProjectFileProtectionStatusValues.NotConfigured);

        Assert.That(replacing.Rows[0].OperationalState, Is.EqualTo("unavailable"));
        Assert.That(replacing.Rows[0].Consequence, Does.Contain("unavailable"));
        Assert.That(replacing.RenderText(), Does.Not.Contain("test.secret.com"));
        Assert.That(ready.Rows[0].OperationalState, Is.EqualTo("ready"));
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
