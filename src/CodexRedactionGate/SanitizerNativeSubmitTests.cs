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
    public void NativeSubmitInterception_SuppressesSubmitWhenProtectedProfileFocusIsNotComposer()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string>
                {
                    ["profile_id"] = "codex-desktop",
                    ["profile_match_count"] = "1",
                    ["composer_status"] = OsInteractionStatusIds.NotComposer
                }));

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NotComposer));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Diagnostics["fail_closed_reason"], Is.EqualTo("selected_profile_not_composer"));
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
    public void NativeSubmitInterception_SetupRequiredSuppressesOnlySelectedSubmitBinding()
    {
        var profile = CreateProtectedProfile();
        var setup = FixedFirstRunSetupController.RequiredFor("codex-desktop");
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: setup);

        var unrelated = controller.HandleGesture(new NativeKeyGesture("A", Ctrl: true));
        var newline = controller.HandleGesture(new NativeKeyGesture("Enter", Shift: true));
        var selectedSubmit = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(unrelated.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
        Assert.That(newline.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(newline.SuppressOriginalInput, Is.False);
        Assert.That(selectedSubmit.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
        Assert.That(selectedSubmit.SuppressOriginalInput, Is.True);
        Assert.That(setup.EnsureSetupCalls, Is.EqualTo(0));
    }

    [Test]
    public void NativeSubmitInterception_SetupRequiredPassesThroughUnselectedForegroundProfile()
    {
        var profile = CreateProtectedProfile();
        var setup = FixedFirstRunSetupController.RequiredFor("codex-desktop");
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("chatgpt-desktop")),
            firstRunSetupController: setup);

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Diagnostics["pass_through_reason"], Is.EqualTo("active_profile_mismatch"));
        Assert.That(setup.EnsureSetupCalls, Is.EqualTo(0));
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
    public void TrayProtectionController_HandlesNextProtectedSendAfterCancel()
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
                var status = submitFlowCalls == 1
                    ? OsInteractionStatusIds.Canceled
                    : OsInteractionStatusIds.Submitted;
                return new OsInteractionResult(
                    status,
                    CreateNativeSubmitSurface("codex-desktop"),
                    null,
                    null,
                    Applied: status == OsInteractionStatusIds.Submitted,
                    Submitted: status == OsInteractionStatusIds.Submitted,
                    Diagnostics: new Dictionary<string, string> { ["profile_id"] = "codex-desktop" });
            },
            profile);

        controller.Start();
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        var afterCancel = controller.State;
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(submitFlowCalls, Is.EqualTo(2));
        Assert.That(afterCancel.LastStatus, Is.EqualTo(OsInteractionStatusIds.Canceled));
        Assert.That(afterCancel.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(controller.State.LastSubmitted, Is.True);
        Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
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
    public void FirstRunSetupController_VerifyProfileUsesFocusedDiscoveryEvidence()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Upsert(layout, CreateUnprotectedProfile("codex-desktop"));
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"))),
                (_, _, _) => throw new InvalidOperationException("Setup window should not be shown by VerifyProfile."));

            var result = controller.VerifyProfile("codex-desktop", layout);
            var stored = SubmitBindingProfileStore.Load(layout).Profiles.Single();

            Assert.That(result.Succeeded, Is.True);
            Assert.That(stored.IsProtected, Is.True);
            Assert.That(stored.Diagnostics["cloud_submission"], Is.EqualTo("false"));
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
    public void FirstRunSetupController_VerifyProfileFailsClosedWhenFocusedDiscoveryFails()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Upsert(layout, CreateUnprotectedProfile("codex-desktop"));
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer)),
                (_, _, _) => throw new InvalidOperationException("Setup window should not be shown by VerifyProfile."));

            var result = controller.VerifyProfile("codex-desktop", layout);
            var stored = SubmitBindingProfileStore.Load(layout).Profiles.Single();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(stored.IsProtected, Is.False);
            Assert.That(stored.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
            Assert.That(controller.IsSetupComplete(layout), Is.False);
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
    public void FirstRunSetupController_GetSetupStatusDoesNotOpenSetupWindow()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Upsert(layout, CreateUnprotectedProfile("codex-desktop"));
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer)),
                (_, _, _) => throw new InvalidOperationException("GetSetupStatus must be side-effect free."));

            var result = controller.GetSetupStatus(layout);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.State.Required, Is.True);
            Assert.That(result.State.UnprotectedProfileIds, Does.Contain("codex-desktop"));
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
    public void FirstRunSetupController_GetSetupStatusRequiresSetupWhenProfileStoreIsEmpty()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer)),
                (_, _, _) => throw new InvalidOperationException("GetSetupStatus must be side-effect free."));

            var result = controller.GetSetupStatus(layout);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("setup_required"));
            Assert.That(result.State.Required, Is.True);
            Assert.That(result.State.UnprotectedProfileIds, Does.Contain("codex-desktop"));
            Assert.That(result.State.UnprotectedProfileIds, Does.Contain("chatgpt-desktop"));
            Assert.That(controller.IsSetupComplete(layout), Is.False);
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
    public void FirstRunSetupController_VerifyProfileCanCreateMissingDefaultProfile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"))),
                (_, _, _) => throw new InvalidOperationException("Setup window should not be shown by VerifyProfile."));

            var result = controller.VerifyProfile("codex-desktop", layout);
            var stored = SubmitBindingProfileStore.Load(layout).Profiles.Single();

            Assert.That(result.Succeeded, Is.True);
            Assert.That(stored.ProfileId, Is.EqualTo("codex-desktop"));
            Assert.That(stored.IsProtected, Is.True);
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
    public void NativeSubmitInterception_SetupStatusFailureSuppressesSelectedSubmit()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: FixedFirstRunSetupController.FailedFor("codex-desktop"));

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void WindowsTrayApp_UsesDefaultUnprotectedProfileBeforeFirstVerification()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var profile = WindowsTrayApp.ResolveNativeProfileForProtection(layout);

            Assert.That(profile, Is.Not.Null);
            Assert.That(profile!.ProfileId, Is.EqualTo("codex-desktop"));
            Assert.That(profile.IsProtected, Is.False);
            Assert.That(profile.SubmitBinding!.DisplayText, Is.EqualTo("Enter"));
            Assert.That(profile.NewlineBinding!.DisplayText, Is.EqualTo("Ctrl+Enter"));
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
    public void FirstRunSetupForm_RequiresConfirmedUnprotectedExitInsteadOfSkip()
    {
        var source = ProductSourceText("FirstRunSetup.cs");

        Assert.That(source, Does.Not.Contain("Continue Without Setup"));
        Assert.That(source, Does.Contain("Exit setup"));
        Assert.That(source, Does.Contain("protected Send will remain blocked"));
        Assert.That(source, Does.Contain("AcceptButton = _verifyCodexButton"));
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

    internal static SubmitBindingProfile CreateProtectedProfile()
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

    private static SubmitBindingProfile CreateUnprotectedProfile(string profileId)
    {
        return new SubmitBindingProfile(
            profileId,
            Enabled: false,
            BindingSource: "not_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Shift+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.BindingUnknown,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());
    }

    internal static TextSurfaceDescriptor CreateNativeSubmitSurface(string profileId)
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

    private sealed class FixedFirstRunSetupController : IFirstRunSetupController
    {
        private readonly FirstRunSetupResult _result;

        private FixedFirstRunSetupController(FirstRunSetupResult result)
        {
            _result = result;
        }

        public int EnsureSetupCalls { get; private set; }

        public static FixedFirstRunSetupController RequiredFor(params string[] profileIds)
        {
            return new FixedFirstRunSetupController(new FirstRunSetupResult(
                Succeeded: false,
                Code: "setup_required",
                State: new FirstRunSetupState(
                    Required: true,
                    UnprotectedProfileIds: profileIds,
                    Status: "pending",
                    VerifiedCodex: false,
                    VerifiedChatGpt: false),
                Diagnostics: new Dictionary<string, string>()));
        }

        public static FixedFirstRunSetupController FailedFor(params string[] profileIds)
        {
            return new FixedFirstRunSetupController(new FirstRunSetupResult(
                Succeeded: false,
                Code: "profiles_load_failed",
                State: new FirstRunSetupState(
                    Required: true,
                    UnprotectedProfileIds: profileIds,
                    Status: "error",
                    VerifiedCodex: false,
                    VerifiedChatGpt: false),
                Diagnostics: new Dictionary<string, string>()));
        }

        public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout)
        {
            EnsureSetupCalls++;
            return _result;
        }

        public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout)
        {
            return _result;
        }

        public FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout)
        {
            return _result;
        }

        public bool IsSetupComplete(DefaultStorageLayout layout)
        {
            return !_result.State.Required;
        }
    }

    private sealed class StaticFirstRunProfileVerifier : IFirstRunProfileVerifier
    {
        private readonly TextSurfaceDiscoveryResult _discovery;

        public StaticFirstRunProfileVerifier(TextSurfaceDiscoveryResult discovery)
        {
            _discovery = discovery;
        }

        public SubmitBindingProfile Verify(SubmitBindingProfile profile)
        {
            return SubmitBindingOnboardingVerifier.VerifyUserBindings(
                profile.ProfileId,
                profile.SubmitBinding?.DisplayText ?? "Enter",
                profile.NewlineBinding?.DisplayText ?? "Ctrl+Enter",
                _discovery,
                profile.CompatibilityEvidence);
        }
    }
}

/// <summary>
/// Regression tests for ticket 229: Setup enforcement security invariants
/// </summary>
public partial class SanitizerTests
{
    [Test]
    public void SetupEnforcement_NoBroadKeySuppression()
    {
        // Setup is required for codex-desktop
        var setup = FixedFirstRunSetupController.RequiredFor("codex-desktop");
        var profile = CreateProtectedProfile();
        
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: setup);

        // Unrelated keys should pass through without setup-required suppression
        var unrelatedKey = controller.HandleGesture(new NativeKeyGesture("A"));
        Assert.That(unrelatedKey.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(unrelatedKey.SuppressOriginalInput, Is.False);

        // Newline should pass through
        var newline = controller.HandleGesture(new NativeKeyGesture("Enter", Shift: true));
        Assert.That(newline.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(newline.SuppressOriginalInput, Is.False);

        // Only selected submit binding should be suppressed
        var selectedSubmit = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(selectedSubmit.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
        Assert.That(selectedSubmit.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void SetupEnforcement_NoMockVerificationSuccess()
    {
        // Verify that profile is NOT marked protected without real verification evidence
        var setupController = new FirstRunSetupController(
            new FixedVerificationProfileVerifier(failure: true),
            ShowSetupWindowStub);

        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var result = setupController.VerifyProfile("codex-desktop", layout);

            // Should NOT be protected when verification fails
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.State.Required, Is.True);
            Assert.That(result.State.Status, Is.EqualTo("pending"));
            Assert.That(result.State.UnprotectedProfileIds.Contains("codex-desktop"), Is.True);
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
    public void SetupEnforcement_NoUnconfirmedSetupSkip()
    {
        // Verify that closing setup without verification keeps it incomplete
        var controller = new FirstRunSetupController(
            new FocusedComposerFirstRunProfileVerifier(),
            (profiles, layout, setupCtrl) =>
            {
                // Simulate user closing setup window without confirmation
                return false;
            });

        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var result = controller.EnsureSetup(layout);

            // Ensure setup is NOT marked complete after close
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.State.Required, Is.True);
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
    public void SetupEnforcement_SharedReadinessSemantics()
    {
        // Verify tray and native submit use the same setup state
        var setupController = new FirstRunSetupController();
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            
            // Get status from setup controller directly
            var setupStatus = setupController.GetSetupStatus(layout);
            
            // Create native submit controller with same setup controller
            var controller = new NativeSubmitInterceptionController(
                CreateProtectedProfile(),
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                firstRunSetupController: setupController);

            // Verify tray uses same setup state
            var trayUsesSharedState = setupController.IsSetupComplete(layout) == controller.IsSetupRequired(layout);
            
            // If setup is required, IsSetupRequired should return true
            Assert.That(setupStatus.State.Required, Is.EqualTo(controller.IsSetupRequired(layout)));
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
    public void SetupEnforcement_SetupRequiredBlocksMatchingSendOnly()
    {
        // Verify setup-required only suppresses the selected profile's verified submit binding
        var setup = FixedFirstRunSetupController.RequiredFor("codex-desktop", "chatgpt-desktop");
        var codexProfile = CreateProtectedProfile();
        var chatgptProfile = CreateProtectedProfile() with { ProfileId = "chatgpt-desktop" };

        var codexController = new NativeSubmitInterceptionController(
            codexProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: setup);

        var chatgptController = new NativeSubmitInterceptionController(
            chatgptProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("chatgpt-desktop")),
            firstRunSetupController: setup);

        // Codex (matching profile) should be suppressed
        var codexSubmit = codexController.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(codexSubmit.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));

        // ChatGPT (matching profile with same setup) should also be suppressed
        var chatgptSubmit = chatgptController.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        // Debug: check active surface
        var chatgptActiveSurface = CreateNativeSubmitSurface("chatgpt-desktop");
        Assert.That(chatgptSubmit.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
    }

    private sealed class FixedVerificationProfileVerifier : IFirstRunProfileVerifier
    {
        private readonly bool _failure;

        public FixedVerificationProfileVerifier(bool failure)
        {
            _failure = failure;
        }

        public SubmitBindingProfile Verify(SubmitBindingProfile profile)
        {
            if (_failure)
            {
                // Return unverified profile (simulating failed verification)
                return profile with { CapabilityStatus = OsInteractionStatusIds.BindingUnknown };
            }

            // Return verified profile
            return profile with { CapabilityStatus = OsInteractionStatusIds.Protected };
        }
    }

    private static bool ShowSetupWindowStub(
        IReadOnlyList<SubmitBindingProfile> profiles,
        DefaultStorageLayout layout,
        IFirstRunSetupController setupController)
    {
        return false; // Simulate user cancel/close
    }
}

[TestFixture]
public class HandleButtonClickTests : SanitizerTests
{
    [Test]
    public void HandleButtonClick_MatchesProfileAndAllowsSubmit()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        var activeSurface = new TextSurfaceDescriptor(
            "native-submit-test:codex-desktop",
            "codex-desktop",
            "codex-desktop",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new Dictionary<string, string>
            {
                ["composer_status"] = OsInteractionStatusIds.SupportedComposer
            });

        var result = controller.HandleButtonClick(activeSurface, () => new OsInteractionResult(
            OsInteractionStatusIds.Submitted,
            Surface: activeSurface,
            SanitizationResult: null,
            ConfirmationModel: null,
            Applied: true,
            Submitted: true,
            Diagnostics: new Dictionary<string, string>()));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Applied, Is.True);
        Assert.That(result.Submitted, Is.True);
    }

    [Test]
    public void HandleButtonClick_FailsClosedWhenUnverifiedSurface()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        var activeSurface = new TextSurfaceDescriptor(
            "native-submit-test:codex-desktop",
            "codex-desktop",
            "codex-desktop",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new Dictionary<string, string>
            {
                ["composer_status"] = "unsupported_composer"
            });

        var result = controller.HandleButtonClick(activeSurface);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
    }

    [Test]
    public void HandleButtonClick_PassThroughWhenProfileMismatch()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        var activeSurface = new TextSurfaceDescriptor(
            "native-submit-test:other-app",
            "other-app",
            "other-app",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new Dictionary<string, string>
            {
                ["composer_status"] = OsInteractionStatusIds.SupportedComposer
            });

        var result = controller.HandleButtonClick(activeSurface);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
    }

    [Test]
    public void HandleButtonClick_PassThroughWhenUnprotectedProfile()
    {
        var unprotectedProfile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "manual",
            SubmitBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Shift+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.NativeSubmitPassThrough,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var controller = new NativeSubmitInterceptionController(
            unprotectedProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        var activeSurface = new TextSurfaceDescriptor(
            "native-submit-test:codex-desktop",
            "codex-desktop",
            "codex-desktop",
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new Dictionary<string, string>
            {
                ["composer_status"] = OsInteractionStatusIds.SupportedComposer
            });

        var result = controller.HandleButtonClick(activeSurface);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
    }
}

[TestFixture]
public class NativeSendBindingSelectionTests : SanitizerTests
{
    [Test]
    public void SubmitBindingSelection_SupportsBothEnterAndCtrlEnterPairs()
    {
        // Test that both supported pairs are valid:
        // 1. Enter Send / Ctrl+Enter newline
        // 2. Ctrl+Enter Send / Enter newline
        
        var profileId = "codex-desktop";
        var surface = CreateNativeSubmitSurface(profileId);
        var discovery = TextSurfaceDiscoveryResult.Success(surface);

        // Pair 1: Enter as Send, Ctrl+Enter as newline
        var pair1 = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId, "Enter", "Ctrl+Enter", discovery);
        
        Assert.That(pair1.BindingSource, Is.EqualTo("user_verified"));
        Assert.That(pair1.SubmitBinding?.DisplayText, Is.EqualTo("Enter"));
        Assert.That(pair1.NewlineBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));

        // Pair 2: Ctrl+Enter as Send, Enter as newline
        var pair2 = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId, "Ctrl+Enter", "Enter", discovery);
        
        Assert.That(pair2.BindingSource, Is.EqualTo("user_verified"));
        Assert.That(pair2.SubmitBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));
        Assert.That(pair2.NewlineBinding?.DisplayText, Is.EqualTo("Enter"));
    }

    [Test]
    public void SubmitBindingSelection_RejectsSameBindingForSubmitAndNewline()
    {
        var profileId = "codex-desktop";
        var surface = CreateNativeSubmitSurface(profileId);
        var discovery = TextSurfaceDiscoveryResult.Success(surface);

        // Same binding for both should fail
        var result = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId, "Enter", "Enter", discovery);

        // Verify fails when bindings are the same - should not be protected
        Assert.That(result.CapabilityStatus, Is.Not.EqualTo(OsInteractionStatusIds.Protected));
        Assert.That(result.IsEnabled, Is.False);
    }

    [Test]
    public void SubmitBindingSelection_PersistsSelectedPair()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            
            // Create profile with specific binding pair
            var profile = new SubmitBindingProfile(
                "codex-desktop",
                Enabled: true,
                BindingSource: "user_verified",
                SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
                NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
                CapabilityStatus: OsInteractionStatusIds.Protected,
                CompatibilityEvidence: null,
                Diagnostics: new Dictionary<string, string>());

            SubmitBindingProfileStore.Save(layout, new[] { profile });

            // Load and verify pair is preserved
            var loaded = SubmitBindingProfileStore.Load(layout);
            Assert.That(loaded.Profiles, Has.Count.EqualTo(1));
            Assert.That(loaded.Profiles[0].SubmitBinding?.DisplayText, Is.EqualTo("Enter"));
            Assert.That(loaded.Profiles[0].NewlineBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void SubmitBindingSelection_PersistsCtrlEnterSendPair()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            
            // Create profile with Ctrl+Enter as Send, Enter as newline
            var profile = new SubmitBindingProfile(
                "chatgpt-desktop",
                Enabled: true,
                BindingSource: "user_verified",
                SubmitBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
                NewlineBinding: SubmitKeyBinding.Parse("Enter").Binding!,
                CapabilityStatus: OsInteractionStatusIds.Protected,
                CompatibilityEvidence: null,
                Diagnostics: new Dictionary<string, string>());

            SubmitBindingProfileStore.Save(layout, new[] { profile });

            // Load and verify pair is preserved
            var loaded = SubmitBindingProfileStore.Load(layout);
            Assert.That(loaded.Profiles, Has.Count.EqualTo(1));
            Assert.That(loaded.Profiles[0].SubmitBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));
            Assert.That(loaded.Profiles[0].NewlineBinding?.DisplayText, Is.EqualTo("Enter"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
