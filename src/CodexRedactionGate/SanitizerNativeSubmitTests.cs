using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

public partial class SanitizerTests
{
    [Test]
    public void SubmitBindingProfileStore_PersistsBindingsAndRawFreeStatus()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var profile = CreateProtectedProfile();

            var save = SubmitBindingProfileStore.Upsert(layout, profile);
            var load = SubmitBindingProfileStore.Load(layout);
            var stored = File.ReadAllText(SubmitBindingProfileStore.DefaultPath(layout));

            Assert.That(save.Succeeded, Is.True);
            Assert.That(load.Succeeded, Is.True);
            Assert.That(load.Profiles, Has.Count.EqualTo(1));
            Assert.That(load.Profiles[0].BindingSource, Is.EqualTo("user_verified"));
            Assert.That(load.Profiles[0].SubmitBinding!.DisplayText, Is.EqualTo("Ctrl+Enter"));
            Assert.That(load.Profiles[0].NewlineBinding!.DisplayText, Is.EqualTo("Shift+Enter"));
            Assert.That(load.Profiles[0].CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
            Assert.That(stored, Does.Not.Contain("192.168.10.25"));
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
    public void SubmitBindingOnboardingVerifier_RecordsSubmitAndNewlineWithoutCloudSubmission()
    {
        var discovery = TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"));

        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Ctrl+Enter",
            "Shift+Enter",
            discovery);

        Assert.That(profile.IsProtected, Is.True);
        Assert.That(profile.BindingSource, Is.EqualTo("user_verified"));
        Assert.That(profile.SubmitBinding!.DisplayText, Is.EqualTo("Ctrl+Enter"));
        Assert.That(profile.NewlineBinding!.DisplayText, Is.EqualTo("Shift+Enter"));
        Assert.That(profile.Diagnostics["cloud_submission"], Is.EqualTo("false"));
    }

    [Test]
    public void SubmitBindingOnboardingVerifier_FailsClosedWhenSubmitAndNewlineAreSame()
    {
        var discovery = TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"));

        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Enter",
            discovery);

        Assert.That(profile.IsProtected, Is.False);
        Assert.That(profile.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.BindingUnknown));
        Assert.That(profile.Diagnostics["binding_error"], Is.EqualTo("submit_newline_same_binding"));
    }

    [Test]
    public void NativeSubmitInterception_GuardsOnlyVerifiedSubmitBinding()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));

        var newline = controller.HandleGesture(new NativeKeyGesture("Enter", Shift: true));
        var unrelated = controller.HandleGesture(new NativeKeyGesture("A", Ctrl: true));
        var submit = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(newline.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(newline.SuppressOriginalInput, Is.False);
        Assert.That(newline.Diagnostics["pass_through_reason"], Is.EqualTo("newline_binding"));
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
        Assert.That(submit.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(submit.SuppressOriginalInput, Is.True);
        Assert.That(submit.Submitted, Is.False);
    }

    [Test]
    public void NativeSubmitInterception_PassesThroughSubmitBindingWhenForegroundIsUnsupported()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.UnsupportedSurface,
                new Dictionary<string, string> { ["unsupported_scope"] = "browser_or_pwa" }));

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Diagnostics["pass_through_reason"], Is.EqualTo("active_surface_not_supported"));
    }

    [Test]
    public void NativeSubmitInterception_PassesThroughSubmitBindingWhenForegroundProfileDiffers()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("chatgpt-desktop")));

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Diagnostics["pass_through_reason"], Is.EqualTo("active_profile_mismatch"));
    }

    [Test]
    public void NativeSubmitInterception_GuardsSubmitBindingOnlyWhenForegroundProfileMatches()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Diagnostics["active_surface_gate"], Is.EqualTo("selected_profile"));
    }

    [Test]
    public void WindowsNativeSubmitHookHost_TreatsSendKeysEventsAsInjected()
    {
        Assert.That(WindowsNativeSubmitHookHost.IsInjectedKeyboardEvent(0x10), Is.True);
        Assert.That(WindowsNativeSubmitHookHost.IsInjectedKeyboardEvent(0x02), Is.True);
        Assert.That(WindowsNativeSubmitHookHost.IsInjectedKeyboardEvent(0), Is.False);
    }

    [Test]
    public void NativeSubmitInterception_ConfirmAndSendSuppressesOriginalAndSubmitsSanitizedFlow()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));
        var flow = NativeSubmitProductSmokeRunner.Run(System.Text.Encoding.UTF8.GetBytes("native-submit-test-secret"));

        var result = controller.HandleGesture(
            new NativeKeyGesture("Enter", Ctrl: true),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                CreateNativeSubmitSurface("codex-desktop"),
                null,
                null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string> { ["flow_kind"] = "test_confirm_and_send" }));

        Assert.That(flow.Passed, Is.True);
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.True);
    }

    [Test]
    public void VerifiedSubmitBindingAction_ReplaysOnlyProfileVerifiedBinding()
    {
        var inner = new CapturingSubmitAction();
        var profile = CreateProtectedProfile();
        var action = new VerifiedSubmitBindingAction(inner, profile);

        var result = action.Submit(CreateNativeSubmitSurface("codex-desktop"));
        var mismatch = action.Submit(CreateNativeSubmitSurface("chatgpt-desktop"));
        var unknown = new VerifiedSubmitBindingAction(
            inner,
            profile with { CapabilityStatus = OsInteractionStatusIds.BindingUnknown })
            .Submit(CreateNativeSubmitSurface("codex-desktop"));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(inner.LastSurface!.Metadata["submit_binding"], Is.EqualTo("Ctrl+Enter"));
        Assert.That(inner.LastSurface.Metadata["submit_binding_sendkeys"], Is.EqualTo("^{ENTER}"));
        Assert.That(mismatch.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(unknown.Status, Is.EqualTo(OsInteractionStatusIds.BindingUnknown));
    }

    [Test]
    public void NativeSubmitInterception_EmergencyDisableAndWatchdogAreRawFree()
    {
        var now = DateTimeOffset.Parse("2026-07-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var profile = CreateProtectedProfile();
        var emergency = new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5));
        var controller = new NativeSubmitInterceptionController(profile, emergency, clock: () => now);

        var disabled = controller.HandleGesture(NativeKeyGesture.CtrlAltShiftPause);
        var afterDisable = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        var unhealthy = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)))
            .HandleGesture(new NativeKeyGesture("Enter", Ctrl: true), hookHealthy: false);
        var serialized = System.Text.Json.JsonSerializer.Serialize(new[] { disabled, afterDisable, unhealthy });

        Assert.That(disabled.Status, Is.EqualTo(OsInteractionStatusIds.EmergencyDisabled));
        Assert.That(disabled.SuppressOriginalInput, Is.True);
        Assert.That(afterDisable.Status, Is.EqualTo(OsInteractionStatusIds.DegradedHotkeyOnly));
        Assert.That(afterDisable.SuppressOriginalInput, Is.False);
        Assert.That(unhealthy.Status, Is.EqualTo(OsInteractionStatusIds.DegradedHotkeyOnly));
        Assert.That(serialized, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void NativeSubmitEnterprisePolicy_CanBlockRequiredProfileDegradation()
    {
        var policy = new NativeSubmitEnterprisePolicy(
            ManagedMode: true,
            RequiredProfileIds: new[] { "codex-desktop" },
            DisallowHotkeyOnlyDegradation: true,
            UnverifiedRequiredProfileBehavior: "block_submit");
        var degradedProfile = CreateProtectedProfile() with
        {
            CapabilityStatus = OsInteractionStatusIds.DegradedHotkeyOnly
        };
        var controller = new NativeSubmitInterceptionController(
            degradedProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            policy);

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true), hookHealthy: false);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.EnterpriseBlocked));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Diagnostics["enterprise_reason"], Is.EqualTo("hotkey_only_degradation_forbidden"));
        Assert.That(result.Diagnostics["raw_prompt_replayed"], Is.EqualTo("false"));
    }

    [Test]
    public void NativeSubmitEnterprisePolicy_DoesNotSuppressNonSubmitKeys()
    {
        var policy = new NativeSubmitEnterprisePolicy(
            ManagedMode: true,
            RequiredProfileIds: new[] { "codex-desktop" },
            DisallowHotkeyOnlyDegradation: true,
            UnverifiedRequiredProfileBehavior: "block_submit");
        var degradedProfile = CreateProtectedProfile() with
        {
            CapabilityStatus = OsInteractionStatusIds.DegradedHotkeyOnly
        };
        var controller = new NativeSubmitInterceptionController(
            degradedProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            policy);

        var result = controller.HandleGesture(new NativeKeyGesture("A", Ctrl: true), hookHealthy: false);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Diagnostics["pass_through_reason"], Is.EqualTo("not_submit_binding"));
    }

    [Test]
    public void TrayProtectionController_StartsNativeHookAndRunsSuppressedSubmitFlow()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var controller = new TrayProtectionController(
            new FakeTrayHotkeyHost(),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Applied,
                CreateNativeSubmitSurface("codex-desktop"),
                null,
                null,
                Applied: true,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()),
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                CreateNativeSubmitSurface("codex-desktop"),
                null,
                null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));

        var started = controller.Start();
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(started, Is.True);
        Assert.That(hook.Started, Is.True);
        Assert.That(controller.State.NativeSubmitEnabled, Is.True);
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(controller.State.LastSubmitted, Is.True);
        Assert.That(controller.State.LastProfileId, Is.EqualTo("codex-desktop"));
        Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
        Assert.That(controller.State.ComposerProtected, Is.True);
    }

    [Test]
    public void TrayProtectionController_RunsSuppressedSubmitFlowForEveryProtectedSend()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitFlowCalls = 0;
        var controller = new TrayProtectionController(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                submitFlowCalls++;
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    CreateNativeSubmitSurface("codex-desktop"),
                    null,
                    null,
                    Applied: true,
                    Submitted: true,
                    Diagnostics: new Dictionary<string, string> { ["profile_id"] = "codex-desktop" });
            },
            profile);

        controller.Start();
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(submitFlowCalls, Is.EqualTo(3));
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(controller.State.LastSubmitted, Is.True);
        Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
        Assert.That(controller.State.ComposerProtected, Is.True);
    }

    [Test]
    public void TrayProtectionController_SuppressesDuplicateSendDuringConfirmCancelAndBlockFlows()
    {
        foreach (var flowStatus in new[]
        {
            OsInteractionStatusIds.Submitted,
            OsInteractionStatusIds.Canceled,
            OsInteractionStatusIds.Blocked,
            OsInteractionStatusIds.FailedClosed
        })
        {
            var hook = new FakeNativeSubmitHookHost();
            var profile = CreateProtectedProfile();
            TrayProtectionController? controller = null;
            var submitFlowCalls = 0;
            var inProgressStatusSeen = false;
            controller = new TrayProtectionController(
                new FakeTrayHotkeyHost(),
                () => throw new InvalidOperationException("Manual scan should not run."),
                hook,
                new NativeSubmitInterceptionController(
                    profile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () =>
                {
                    submitFlowCalls++;
                    if (submitFlowCalls == 1)
                    {
                        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
                        inProgressStatusSeen = controller!.State.LastStatus == OsInteractionStatusIds.NativeSubmitInProgress
                            && controller.State.LastSubmitted == false
                            && controller.State.NativeSubmitStatus == OsInteractionStatusIds.Protected;
                    }

                    return new OsInteractionResult(
                        flowStatus,
                        CreateNativeSubmitSurface("codex-desktop"),
                        null,
                        null,
                        Applied: flowStatus == OsInteractionStatusIds.Submitted,
                        Submitted: flowStatus == OsInteractionStatusIds.Submitted,
                        Diagnostics: new Dictionary<string, string> { ["profile_id"] = "codex-desktop" });
                },
                profile);

            controller.Start();
            hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
            hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

            Assert.That(submitFlowCalls, Is.EqualTo(2), flowStatus);
            Assert.That(inProgressStatusSeen, Is.True, flowStatus);
            Assert.That(controller.State.LastStatus, Is.EqualTo(flowStatus), flowStatus);
            Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected), flowStatus);
            Assert.That(controller.State.ComposerProtected, Is.True, flowStatus);
        }
    }

    [Test]
    public void SurfaceCompatibilityEvaluator_WarnsWhenSelectedAppVersionOrProfileNoLongerMatches()
    {
        var profile = CreateProtectedProfile();

        var mismatch = SurfaceCompatibilityEvaluator.Evaluate(
            profile,
            CreateNativeSubmitSurface("chatgpt-desktop"),
            null);

        Assert.That(mismatch.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(mismatch.Diagnostics["mismatch_reason"], Is.EqualTo("profile_id_mismatch"));
    }

    [Test]
    public void NativeSubmitProductSmoke_CoversProfileSetupGuardFlowEmergencyEnterpriseAndMismatch()
    {
        var report = NativeSubmitProductSmokeRunner.Run(System.Text.Encoding.UTF8.GetBytes("native-submit-smoke-secret"));
        var rendered = string.Join(Environment.NewLine, NativeSubmitProductSmokeRunner.RenderRawFree(report));

        Assert.That(report.Passed, Is.True);
        Assert.That(report.ProfileSetupPassed, Is.True);
        Assert.That(report.BindingVerificationPassed, Is.True);
        Assert.That(report.GuardPassed, Is.True);
        Assert.That(report.ConfirmAndSendPassed, Is.True);
        Assert.That(report.RepeatedSubmitPassed, Is.True);
        Assert.That(report.DuplicateSendGuardPassed, Is.True);
        Assert.That(report.OverlayForegroundRequestPassed, Is.True);
        Assert.That(report.OverlayForegroundRefusalStatusPassed, Is.True);
        Assert.That(report.EmergencyDisablePassed, Is.True);
        Assert.That(report.EnterpriseEnforcementPassed, Is.True);
        Assert.That(report.MismatchWarningPassed, Is.True);
        Assert.That(rendered, Does.Contain("windows_codex_chatgpt_desktop_only"));
        Assert.That(rendered, Does.Contain("repeated_submit_confirmation: true"));
        Assert.That(rendered, Does.Contain("duplicate_send_guard: true"));
        Assert.That(rendered, Does.Contain("overlay_foreground_request: true"));
        Assert.That(rendered, Does.Contain("overlay_foreground_refusal_status: true"));
        Assert.That(rendered, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void Program_NativeSubmitSmoke_PrintsRawFreeNativeSubmitStatus()
    {
        var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
            Program.Main(new[] { "--native-submit-smoke" }, TestSanitizers.Create));

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stderr, Is.Empty);
        Assert.That(stdout, Does.Contain("native_submit_status: native_submit_smoke_passed"));
        Assert.That(stdout, Does.Contain("guard_interception: true"));
        Assert.That(stdout, Does.Contain("repeated_submit_confirmation: true"));
        Assert.That(stdout, Does.Contain("duplicate_send_guard: true"));
        Assert.That(stdout, Does.Contain("overlay_foreground_request: true"));
        Assert.That(stdout, Does.Contain("overlay_foreground_refusal_status: true"));
        Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
    }

    [Test]
    public void Program_NativeProfilesStatus_PrintsRawFreeDiagnostics()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Upsert(layout, CreateProtectedProfile());
            var runtime = new CliRuntime(
                _ => TestSanitizers.Create(),
                () => Sanitizer.LoadProductionPolicy(layout),
                Sanitizer.CreateProduction,
                () => layout,
                LocalRestoreWorkflow.CreateProduction);

            var (exitCode, stdout, stderr) = CaptureProgramOutput(() =>
                Program.Main(new[] { "--native-profiles-status" }, runtime));

            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(stderr, Is.Empty);
            Assert.That(stdout, Does.Contain("profile=codex-desktop"));
            Assert.That(stdout, Does.Contain("capability_status=protected"));
            Assert.That(stdout, Does.Not.Contain("192.168.10.25"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static SubmitBindingProfile CreateProtectedProfile()
    {
        return new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Shift+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>
            {
                ["verification_mode"] = "user_verified_dry_run",
                ["cloud_submission"] = "false",
                ["package_version"] = "26.715.2305.0",
                ["control_type"] = "ControlType.Group"
            });
    }

    private static TextSurfaceDescriptor CreateNativeSubmitSurface(string profileId)
    {
        return new TextSurfaceDescriptor(
            $"native-submit-test:{profileId}",
            profileId,
            profileId,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new Dictionary<string, string>
            {
                ["composer_status"] = OsInteractionStatusIds.SupportedComposer,
                ["surface_kind"] = "test"
            });
    }

    private sealed class CapturingSubmitAction : ISubmitAction
    {
        public TextSurfaceDescriptor? LastSurface { get; private set; }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            LastSurface = surface;
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string>
            {
                ["submit_binding"] = surface.Metadata.TryGetValue("submit_binding", out var binding)
                    ? binding
                    : "unknown"
            });
        }
    }

    private sealed class FakeNativeSubmitHookHost : INativeSubmitHookHost
    {
        private Func<NativeKeyGesture, NativeSubmitInterceptionResult>? _classify;
        private Action<NativeKeyGesture>? _onSuppressedSubmit;

        public bool Started { get; private set; }

        public string? LastErrorCode { get; private set; }

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture> onSuppressedSubmit)
        {
            _classify = classify;
            _onSuppressedSubmit = onSuppressedSubmit;
            Started = true;
            return true;
        }

        public void Stop()
        {
            Started = false;
            _classify = null;
            _onSuppressedSubmit = null;
        }

        public void Trigger(NativeKeyGesture gesture)
        {
            var result = _classify!(gesture);
            if (result.Status == OsInteractionStatusIds.NativeSubmitGuarded)
            {
                _onSuppressedSubmit!(gesture);
            }
        }
    }

    private sealed class FakeTrayHotkeyHost : ITrayHotkeyHost
    {
        public HotkeyBinding Binding { get; } = new("fake-hotkey", "Ctrl+Enter", "test");

        public string? LastErrorCode { get; private set; }

        public bool Start(Action onTriggered)
        {
            return true;
        }

        public void Stop()
        {
        }
    }
}
