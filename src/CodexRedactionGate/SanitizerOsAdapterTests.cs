using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

public partial class SanitizerTests
{
    [Test]
    public void OsInteractionContracts_ArePlatformNeutralAndRawFree()
    {
        var surface = CreateFakeTextSurface("Normal prompt text");

        var serializedSurface = System.Text.Json.JsonSerializer.Serialize(surface.Surface);

        Assert.That(surface.Surface.CanCaptureText, Is.True);
        Assert.That(surface.Surface.CanReplaceText, Is.True);
        Assert.That(surface.Surface.CanSubmit, Is.True);
        Assert.That(serializedSurface, Does.Not.Contain("Automation"));
        Assert.That(serializedSurface, Does.Not.Contain("Normal prompt text"));
    }

    [Test]
    public void OsInteractionOrchestrator_SafePromptCanApplyAndSubmitThroughFakeAdapter()
    {
        var surface = CreateFakeTextSurface("Normal prompt text");
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.True);
        Assert.That(surface.CurrentText, Is.EqualTo("Normal prompt text"));
        Assert.That(surface.WriteCount, Is.EqualTo(0));
        Assert.That(surface.SubmitCount, Is.EqualTo(1));
        Assert.That(result.Diagnostics["write_status"], Is.EqualTo("skipped_no_changes"));
    }

    [Test]
    public void OsInteractionOrchestrator_SensitivePromptConfirmsAndAppliesOnlySanitizedText()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25");
        var overlay = new FakeConfirmationOverlay(ConfirmationDecisionContract.Confirm);
        var orchestrator = CreateOsOrchestrator(surface, overlay);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Applied));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Does.Contain("IP_"));
        Assert.That(surface.CurrentText, Does.Not.Contain("192.168.10.25"));
        Assert.That(overlay.Models, Has.Count.EqualTo(1));
        Assert.That(result.ConfirmationModel!.SanitizedPrompt, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void OsInteractionOrchestrator_CancelAppliesAndSubmitsNothing()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25");
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Cancel);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Canceled));
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Is.EqualTo("Connect to 192.168.10.25"));
        Assert.That(surface.SubmitCount, Is.EqualTo(0));
    }

    [Test]
    public void OsInteractionOrchestrator_EditedOverlayTextIsLocallyVerifiedBeforeWriteBack()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25");
        var orchestrator = CreateOsOrchestrator(
            surface,
            _ => new ConfirmationDecision(true, new ApprovedSanitizedPayload("Connect to secure-server")));

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(surface.CurrentText, Is.EqualTo("Connect to secure-server"));
        Assert.That(surface.SubmitCount, Is.EqualTo(1));
        Assert.That(result.Diagnostics["edited_text_verified"], Is.EqualTo("true"));
    }

    [Test]
    public void OsInteractionOrchestrator_EditedOverlayTextThatStillRequiresConfirmationFailsClosed()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25");
        var orchestrator = CreateOsOrchestrator(
            surface,
            _ => new ConfirmationDecision(true, new ApprovedSanitizedPayload("Connect to 10.20.30.40")));

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Is.EqualTo("Connect to 192.168.10.25"));
        Assert.That(surface.WriteCount, Is.EqualTo(0));
        Assert.That(surface.SubmitCount, Is.EqualTo(0));
        Assert.That(result.Diagnostics["edited_text_verified"], Is.EqualTo("false"));
        Assert.That(result.Diagnostics["edited_text_status"], Is.EqualTo("requires_confirmation"));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(result.Diagnostics), Does.Not.Contain("10.20.30.40"));
    }

    [Test]
    public void OsInteractionOrchestrator_BlockAppliesAndSubmitsNothing()
    {
        var surface = CreateFakeTextSurface("Reject BLOCK_THIS");
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Blocked));
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.CurrentText, Is.EqualTo("Reject BLOCK_THIS"));
        Assert.That(surface.SubmitCount, Is.EqualTo(0));
    }

    [Test]
    public void OsInteractionOrchestrator_DiagnosticsDoNotExposeRawPromptValues()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25");
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);
        var serializedDiagnostics = System.Text.Json.JsonSerializer.Serialize(result.Diagnostics);

        Assert.That(serializedDiagnostics, Does.Not.Contain("192.168.10.25"));
        Assert.That(serializedDiagnostics, Does.Not.Contain(surface.CurrentText));
        Assert.That(result.Diagnostics["captured_length"], Is.EqualTo("24"));
        Assert.That(result.Diagnostics["decision"], Is.EqualTo("confirm"));
    }

    [Test]
    public void SurfaceProfileCatalog_IncludesCodexAndChatGptProfiles()
    {
        var profiles = SurfaceProfileCatalog.Default.Profiles;
        var serialized = System.Text.Json.JsonSerializer.Serialize(profiles);

        Assert.That(profiles.Select(profile => profile.ProfileId), Does.Contain("codex-desktop"));
        Assert.That(profiles.Select(profile => profile.ProfileId), Does.Contain("chatgpt-desktop"));
        Assert.That(profiles.Select(profile => profile.ProfileId), Does.Contain("redaction-gate-demo"));
        Assert.That(serialized, Does.Not.Contain("SENSITIVE_MARKER"));
        Assert.That(serialized, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void SurfaceCompatibilityMatrix_NamesOnlyWindowsDesktopV1TargetsRawFree()
    {
        var rendered = string.Join(Environment.NewLine, SurfaceCompatibilityMatrix.Render());

        Assert.That(rendered, Does.Contain("compatibility_scope: windows_codex_chatgpt_desktop_only"));
        Assert.That(rendered, Does.Contain("profile=codex-desktop"));
        Assert.That(rendered, Does.Contain("profile=chatgpt-desktop"));
        Assert.That(rendered, Does.Contain("channel=\"Windows desktop\""));
        Assert.That(rendered, Does.Contain("read_only_diagnostic,dry_run,apply_only"));
        Assert.That(rendered, Does.Contain("unsupported_v1: browser,chrome,pwa,whole_window_capture"));
        Assert.That(rendered, Does.Not.Contain("192.168.10.25"));
        Assert.That(rendered, Does.Not.Contain("SENSITIVE_MARKER"));
    }

    [Test]
    public void SurfaceProfileCatalog_DoesNotSupportBrowserOrPwaScopeInV1()
    {
        var browserMatch = SurfaceProfileCatalog.Default.Match("ChatGPT - Google Chrome", "chrome");
        var pwaMatch = SurfaceProfileCatalog.Default.Match("ChatGPT", "msedge");

        Assert.That(browserMatch.Matched, Is.False);
        Assert.That(browserMatch.Status, Is.EqualTo(OsInteractionStatusIds.UnsupportedSurface));
        Assert.That(browserMatch.Diagnostics["unsupported_scope"], Is.EqualTo("browser_or_pwa"));
        Assert.That(pwaMatch.Matched, Is.False);
        Assert.That(pwaMatch.Status, Is.EqualTo(OsInteractionStatusIds.UnsupportedSurface));
        Assert.That(SurfaceCompatibilityMatrix.UnsupportedV1Scopes, Does.Contain("pwa"));
    }

    [Test]
    public void WindowsActiveSurfaceDiscovery_MatchesCodexProfileWithoutPromptText()
    {
        var discovery = new WindowsActiveSurfaceDiscovery(
            SurfaceProfileCatalog.Default,
            new FakeForegroundWindowSnapshotProvider(new ForegroundWindowSnapshot(
                true,
                OsInteractionStatusIds.SupportedSurface,
                "Codex",
                "Codex",
                "Chrome_WidgetWin_1",
                new IntPtr(0x1234))));

        var result = discovery.DiscoverActiveSurface();
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Surface!.ProfileId, Is.EqualTo("codex-desktop"));
        Assert.That(result.Surface.CanCaptureText, Is.True);
        Assert.That(result.Surface.CanReplaceText, Is.True);
        Assert.That(result.Surface.CanSubmit, Is.True);
        Assert.That(serialized, Does.Not.Contain("prompt"));
    }

    [Test]
    public void WindowsActiveSurfaceDiscovery_MatchesChatGptProfileWithoutAmbiguity()
    {
        var discovery = new WindowsActiveSurfaceDiscovery(
            SurfaceProfileCatalog.Default,
            new FakeForegroundWindowSnapshotProvider(new ForegroundWindowSnapshot(
                true,
                OsInteractionStatusIds.SupportedSurface,
                "ChatGPT",
                "ChatGPT",
                "Chrome_WidgetWin_1",
                new IntPtr(0x5678))));

        var result = discovery.DiscoverActiveSurface();

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Surface!.ProfileId, Is.EqualTo("chatgpt-desktop"));
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SupportedSurface));
    }

    [Test]
    public void WindowsActiveSurfaceDiscovery_UnsupportedPlatformIsRawFree()
    {
        var discovery = new WindowsActiveSurfaceDiscovery(
            SurfaceProfileCatalog.Default,
            new UnsupportedForegroundWindowSnapshotProvider());

        var result = discovery.DiscoverActiveSurface();

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.UnsupportedPlatform));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(result), Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void WindowsFocusedComposerDiscovery_MatchesFocusedWritableComposer()
    {
        var discovery = new WindowsFocusedComposerDiscovery(
            SurfaceProfileCatalog.Default,
            new FakeFocusedElementSnapshotProvider(CreateFocusedElementSnapshot(
                windowTitle: "Codex",
                processName: "Codex",
                controlType: "ControlType.Edit",
                canReadValue: true,
                canWriteValue: true)));

        var result = discovery.DiscoverActiveSurface();
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SupportedComposer));
        Assert.That(result.Surface!.ProfileId, Is.EqualTo("codex-desktop"));
        Assert.That(result.Surface.Metadata["composer_status"], Is.EqualTo(OsInteractionStatusIds.SupportedComposer));
        Assert.That(result.Surface.Metadata["read_strategy"], Is.EqualTo("windows-ui-automation-value-pattern"));
        Assert.That(result.Surface.Metadata["write_strategy"], Is.EqualTo("windows-ui-automation-value-pattern"));
        Assert.That(serialized, Does.Not.Contain("Connect to 192.168.10.25"));
    }

    [Test]
    public void WindowsFocusedComposerDiscovery_RejectsNonComposerFocusedElement()
    {
        var discovery = new WindowsFocusedComposerDiscovery(
            SurfaceProfileCatalog.Default,
            new FakeFocusedElementSnapshotProvider(CreateFocusedElementSnapshot(
                windowTitle: "Codex",
                processName: "Codex",
                controlType: "ControlType.List",
                canReadValue: false,
                canWriteValue: false)));

        var result = discovery.DiscoverActiveSurface();

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NotComposer));
        Assert.That(result.Diagnostics["composer_status"], Is.EqualTo(OsInteractionStatusIds.NotComposer));
    }

    [Test]
    public void WindowsFocusedComposerDiscovery_AcceptsElectronTextPatternComposer()
    {
        var discovery = new WindowsFocusedComposerDiscovery(
            SurfaceProfileCatalog.Default,
            new FakeFocusedElementSnapshotProvider(CreateFocusedElementSnapshot(
                windowTitle: "Codex",
                processName: "Codex",
                controlType: "ControlType.Document",
                canReadValue: false,
                canWriteValue: false,
                canReadTextPattern: true,
                canUseKeyboardTextInput: true)));

        var result = discovery.DiscoverActiveSurface();

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SupportedComposer));
        Assert.That(result.Surface!.CanCaptureText, Is.True);
        Assert.That(result.Surface.CanReplaceText, Is.True);
        Assert.That(result.Surface.Metadata["read_strategy"], Is.EqualTo("windows-ui-automation-text-pattern"));
        Assert.That(result.Surface.Metadata["write_strategy"], Is.EqualTo("verified-composer-keyboard-paste"));
        Assert.That(result.Surface.Metadata["classification_reason"], Is.EqualTo("text_pattern_read_keyboard_write"));
    }

    [Test]
    public void WindowsFocusedComposerDiscovery_AcceptsChromeGroupTextPatternComposer()
    {
        var discovery = new WindowsFocusedComposerDiscovery(
            SurfaceProfileCatalog.Default,
            new FakeFocusedElementSnapshotProvider(CreateFocusedElementSnapshot(
                windowTitle: "ChatGPT",
                processName: "ChatGPT",
                controlType: "ControlType.Group",
                canReadValue: false,
                canWriteValue: false,
                canReadTextPattern: true,
                canUseKeyboardTextInput: true,
                frameworkId: "Chrome",
                className: "Chrome_RenderWidgetHostHWND")));

        var result = discovery.DiscoverActiveSurface();

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SupportedComposer));
        Assert.That(result.Surface!.ProfileId, Is.EqualTo("chatgpt-desktop"));
        Assert.That(result.Surface.CanCaptureText, Is.True);
        Assert.That(result.Surface.CanReplaceText, Is.True);
        Assert.That(result.Surface.Metadata["classification_reason"], Is.EqualTo("known_framework_group_text_pattern_keyboard_write"));
    }

    [Test]
    public void WindowsFocusedComposerDiscovery_MatchesDisposableDemoTarget()
    {
        var discovery = new WindowsFocusedComposerDiscovery(
            SurfaceProfileCatalog.Default,
            new FakeFocusedElementSnapshotProvider(CreateFocusedElementSnapshot(
                windowTitle: "Redaction Gate Demo Target",
                processName: "CodexRedactionGate",
                controlType: "ControlType.Edit",
                canReadValue: true,
                canWriteValue: true)));

        var result = discovery.DiscoverActiveSurface();

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Surface!.ProfileId, Is.EqualTo("redaction-gate-demo"));
        Assert.That(result.Surface.CanSubmit, Is.False);
    }

    [Test]
    public void WindowsVerifiedComposerSurfaceAdapter_RejectsUnverifiedSurfaceBeforeTextAccess()
    {
        var access = new CountingVerifiedComposerTextAccess();
        var adapter = new WindowsVerifiedComposerSurfaceAdapter(access);
        var surface = new TextSurfaceDescriptor(
            "window-only",
            "codex-desktop",
            "Codex Desktop",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new Dictionary<string, string> { ["surface_kind"] = "window-only" });

        var capture = adapter.CaptureText(surface);
        var replace = adapter.ReplaceText(surface, "sanitized");
        var submit = adapter.Submit(surface);

        Assert.That(capture.Status, Is.EqualTo(OsInteractionStatusIds.NotComposer));
        Assert.That(replace.Status, Is.EqualTo(OsInteractionStatusIds.NotComposer));
        Assert.That(submit.Status, Is.EqualTo(OsInteractionStatusIds.NotComposer));
        Assert.That(access.CallCount, Is.EqualTo(0));
    }

    [Test]
    public void WindowsHotkeyDemoLoop_UsesVerifiedComposerAdapterByDefault()
    {
        Assert.That(WindowsHotkeyDemoLoop.LiveAdapterKind, Is.EqualTo("verified-composer"));
        Assert.That(WindowsHotkeyDemoLoop.DefaultHotkeyDisplayText, Is.EqualTo("Ctrl+Enter"));
    }

    [Test]
    public void LiveOsDemoEvidence_DisablesSendUntilLocalSettingAndSupportedApplyEvidenceExist()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var disabled = LiveOsDemoEvidence.Check(layout);
            Assert.That(disabled.Enabled, Is.False);
            Assert.That(disabled.Status, Is.EqualTo(OsInteractionStatusIds.SafetyDisabled));
            Assert.That(disabled.Diagnostics["send_mode_setting_enabled"], Is.EqualTo("false"));

            var missingEvidence = LiveOsDemoEvidence.EnableSendMode(layout);
            Assert.That(missingEvidence.Enabled, Is.False);
            Assert.That(missingEvidence.Status, Is.EqualTo(OsInteractionStatusIds.EvidenceMissing));

            LiveOsDemoEvidence.MarkApplyOnlyPassed("redaction-gate-demo", layout);
            var unsupportedEvidence = LiveOsDemoEvidence.EnableSendMode(layout);
            Assert.That(unsupportedEvidence.Enabled, Is.False);
            Assert.That(unsupportedEvidence.Status, Is.EqualTo(OsInteractionStatusIds.EvidenceMissing));
            Assert.That(unsupportedEvidence.Diagnostics["supported_apply_evidence_present"], Is.EqualTo("false"));

            LiveOsDemoEvidence.MarkApplyOnlyPassed("codex-desktop", layout);
            var enabled = LiveOsDemoEvidence.EnableSendMode(layout);
            Assert.That(enabled.Enabled, Is.True);
            Assert.That(enabled.Status, Is.EqualTo("send_gate_enabled"));
            Assert.That(enabled.Diagnostics["send_mode_setting_enabled"], Is.EqualTo("true"));

            var disabledAgain = LiveOsDemoEvidence.DisableSendMode(layout);
            Assert.That(disabledAgain.Enabled, Is.False);
            Assert.That(disabledAgain.Status, Is.EqualTo(OsInteractionStatusIds.SafetyDisabled));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void OsInteractionOrchestrator_DryRunShowsStatusWithoutModifyingComposer()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25");
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.DryRunOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.DryRunConfirm));
        Assert.That(result.ConfirmationModel, Is.Not.Null);
        Assert.That(surface.CurrentText, Is.EqualTo("Connect to 192.168.10.25"));
        Assert.That(surface.SubmitCount, Is.EqualTo(0));
    }

    [Test]
    public void OsConfirmationOverlayRenderer_ShowsActionsHighlightsCountsAndWarnings()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(TestSecret()));
        var sanitizeResult = sanitizer.Sanitize(CreatePromptRequest("api_key=sk_live_1234567890abcdef"));
        var model = ConfirmationUiShell.CreateModel(sanitizeResult);

        var rendered = OsConfirmationOverlayRenderer.RenderText(model);

        Assert.That(rendered, Does.Contain("Confirm sanitized prompt"));
        Assert.That(rendered, Does.Contain("Cancel"));
        Assert.That(rendered, Does.Contain("[[TOKEN_REDACTED:token]]"));
        Assert.That(rendered, Does.Contain("token: 1"));
        Assert.That(rendered, Does.Contain("Non-restorable secret redaction present."));
        Assert.That(rendered, Does.Contain("raw_values_visible: false"));
        Assert.That(rendered, Does.Not.Contain("sk_live_1234567890abcdef"));
    }

    [Test]
    public void OsInteractionOrchestrator_WriteBackFailureFailsClosedWithoutSubmit()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25", failWrites: true);
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.WriteFailed));
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.SubmitCount, Is.EqualTo(0));
    }

    [Test]
    public void OsInteractionOrchestrator_SendModeVerifiesAppliedTextBeforeSubmit()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25", staleAfterWrite: true);
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.VerificationFailed));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.SubmitCount, Is.EqualTo(0));
    }

    [Test]
    public void OsInteractionOrchestrator_ApplyOnlyVerifiesAppliedTextBeforeEvidence()
    {
        var surface = CreateFakeTextSurface("Connect to 192.168.10.25", staleAfterWrite: true);
        var orchestrator = CreateOsOrchestrator(surface, ConfirmationDecisionContract.Confirm);

        var result = orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.VerificationFailed));
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.False);
        Assert.That(surface.SubmitCount, Is.EqualTo(0));
    }

    [Test]
    public void OsAdapterDemoRunner_SmokeCoversRawFreeUxPaths()
    {
        var report = OsAdapterDemoRunner.RunSmoke(TestSecret());

        Assert.That(report.Passed, Is.True);
        Assert.That(report.DryRunPassed, Is.True);
        Assert.That(report.ApplyOnlyPassed, Is.True);
        Assert.That(report.ConfirmAndSendDisabledByDefaultPassed, Is.True);
        Assert.That(report.ConfirmAndSendPassed, Is.True);
        Assert.That(report.CancelPassed, Is.True);
        Assert.That(report.BlockPassed, Is.True);
        Assert.That(report.WriteFailurePassed, Is.True);
        Assert.That(report.AuditRawFreePassed, Is.True);
    }

    [Test]
    public void Program_OsProfilesList_PrintsRawFreeProfileSummaries()
    {
        var (exitCode, stdout, _) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--os-profiles-list" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("codex-desktop"));
        Assert.That(stdout, Does.Contain("chatgpt-desktop"));
        Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void Program_OsCompatibilityMatrix_PrintsRawFreeSupportedDesktopEvidence()
    {
        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--os-compatibility-matrix" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("windows_codex_chatgpt_desktop_only"));
        Assert.That(stdout, Does.Contain("profile=codex-desktop"));
        Assert.That(stdout, Does.Contain("profile=chatgpt-desktop"));
        Assert.That(stdout, Does.Contain("unsupported_v1: browser,chrome,pwa,whole_window_capture"));
        Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void Program_OsDemoDryRun_ShowsOverlayPreviewWithoutRawSensitiveValue()
    {
        var (exitCode, stdout, _) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--os-demo-dry-run", "Connect to 192.168.10.25" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("status: dry_run_confirm"));
        Assert.That(stdout, Does.Contain("Confirm sanitized prompt"));
        Assert.That(stdout, Does.Contain("IP_"));
        Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void Program_OsDemoSmoke_ReportsGreenUxDemoMatrix()
    {
        var (exitCode, stdout, _) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--os-demo-smoke" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout, Does.Contain("passed: true"));
        Assert.That(stdout, Does.Contain("audit_raw_free: true"));
    }

    [Test]
    public void Program_OsComposerDiagnosticDelay_InvalidArgumentFailsClearly()
    {
        var (exitCode, _, stderr) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--os-composer-diagnostic-delay", "bad" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(1));
        Assert.That(stderr, Does.Contain("Expected non-negative delay in seconds."));
    }

    private static FakeTextSurface CreateFakeTextSurface(
        string text,
        bool failWrites = false,
        bool staleAfterWrite = false)
    {
        return new FakeTextSurface(text, failWrites, staleAfterWrite);
    }

    private static OsInteractionOrchestrator CreateOsOrchestrator(
        FakeTextSurface surface,
        Func<ConfirmationUiModel, ConfirmationDecision> decisionFactory)
    {
        return CreateOsOrchestrator(surface, new FakeConfirmationOverlay(decisionFactory));
    }

    private static OsInteractionOrchestrator CreateOsOrchestrator(
        FakeTextSurface surface,
        IConfirmationOverlay overlay)
    {
        return new OsInteractionOrchestrator(
            new Sanitizer(new InMemoryHmacMappingVault(TestSecret())),
            surface,
            surface,
            surface,
            surface,
            overlay);
    }

    private sealed class FakeTextSurface :
        IActiveTextSurfaceDiscovery,
        ITextSurfaceReader,
        ITextSurfaceWriter,
        ISubmitAction
    {
        private readonly bool _failWrites;
        private readonly bool _staleAfterWrite;
        private readonly string _initialText;

        public FakeTextSurface(string currentText, bool failWrites, bool staleAfterWrite)
        {
            CurrentText = currentText;
            _initialText = currentText;
            _failWrites = failWrites;
            _staleAfterWrite = staleAfterWrite;
            Surface = new TextSurfaceDescriptor(
                SurfaceId: "fake-surface",
                ProfileId: "fake-ai-app",
                DisplayName: "Fake AI App",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                Metadata: new Dictionary<string, string>
                {
                    ["window_title_hash"] = "test-window",
                    ["surface_kind"] = "fake"
                });
        }

        public string CurrentText { get; private set; }

        public int SubmitCount { get; private set; }

        public TextSurfaceDescriptor Surface { get; }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            return TextSurfaceDiscoveryResult.Success(Surface);
        }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
        {
            var capturedText = _staleAfterWrite && WriteCount > 0 ? _initialText : CurrentText;
            return new TextCaptureResult(
                true,
                "captured",
                capturedText,
                new Dictionary<string, string> { ["capture_length"] = capturedText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            if (_failWrites)
            {
                return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
            }

            CurrentText = text;
            WriteCount++;
            return new TextReplacementResult(true, "applied", new Dictionary<string, string> { ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            SubmitCount++;
            return new SubmitActionResult(true, "submitted", new Dictionary<string, string> { ["submit_count"] = SubmitCount.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public int WriteCount { get; private set; }
    }

    private sealed class FakeConfirmationOverlay : IConfirmationOverlay
    {
        private readonly Func<ConfirmationUiModel, ConfirmationDecision> _decisionFactory;

        public FakeConfirmationOverlay(Func<ConfirmationUiModel, ConfirmationDecision> decisionFactory)
        {
            _decisionFactory = decisionFactory;
        }

        public List<ConfirmationUiModel> Models { get; } = new();

        public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
        {
            Models.Add(model);
            return _decisionFactory(model);
        }
    }

    private sealed class FakeForegroundWindowSnapshotProvider : IForegroundWindowSnapshotProvider
    {
        private readonly ForegroundWindowSnapshot _snapshot;

        public FakeForegroundWindowSnapshotProvider(ForegroundWindowSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public ForegroundWindowSnapshot GetForegroundWindow()
        {
            return _snapshot;
        }
    }

    private sealed class FakeFocusedElementSnapshotProvider : IFocusedElementSnapshotProvider
    {
        private readonly FocusedElementSnapshot _snapshot;

        public FakeFocusedElementSnapshotProvider(FocusedElementSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public FocusedElementSnapshot GetFocusedElement()
        {
            return _snapshot;
        }
    }

    private sealed class CountingVerifiedComposerTextAccess : IVerifiedComposerTextAccess
    {
        public int CallCount { get; private set; }

        public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
        {
            CallCount++;
            return new TextCaptureResult(true, "captured", "text", new Dictionary<string, string>());
        }

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            CallCount++;
            return new TextReplacementResult(true, OsInteractionStatusIds.Applied, new Dictionary<string, string>());
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            CallCount++;
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string>());
        }
    }

    private static FocusedElementSnapshot CreateFocusedElementSnapshot(
        string windowTitle,
        string processName,
        string controlType,
        bool canReadValue,
        bool canWriteValue,
        bool canReadTextPattern = true,
        bool canUseKeyboardTextInput = true,
        string frameworkId = "Win32",
        string className = "TextBox")
    {
        return new FocusedElementSnapshot(
            true,
            OsInteractionStatusIds.SupportedSurface,
            windowTitle,
            processName,
            "Chrome_WidgetWin_1",
            new IntPtr(0x1234),
            controlType,
            className,
            "composer",
            frameworkId,
            HasKeyboardFocus: true,
            IsKeyboardFocusable: true,
            IsEnabled: true,
            IsPassword: false,
            CanReadValue: canReadValue,
            CanWriteValue: canWriteValue,
            IsValueReadOnly: !canWriteValue,
            CanReadTextPattern: canReadTextPattern,
            CanUseKeyboardTextInput: canUseKeyboardTextInput,
            ElementRuntimeIdHash: "focusedhash");
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureProgramOutput(Func<int> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exitCode = action();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
