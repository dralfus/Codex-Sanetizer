using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using CodexRedactionGate;

public partial class SanitizerTests
{
    internal static TextSurfaceDiscoveryResult CreateVerifiedChatGptDiscovery()
    {
        return TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("chatgpt-desktop"), new Dictionary<string, string>
        {
            ["application_identity_hash"] = "test-application-hash",
            ["application_version_hash"] = "test-version-hash",
            ["application_version_status"] = "available",
            ["package_full_name_hash"] = "test-package-hash",
            ["executable_name_hash"] = "test-executable-hash",
            ["process_name_hash"] = "test-process-hash",
            ["window_identity_hash"] = "test-window-hash",
            ["window_class_hash"] = "test-window-class-hash",
            ["composer_class_hash"] = "test-composer-class-hash",
            ["element_control_type"] = "ControlType.Group",
            ["element_framework_id"] = "Chrome",
            ["focused_element_hash"] = "test-composer-hash",
            [SendControlEvidence.AutomationIdHashKey] = "test-send-automation-hash",
            [SendControlEvidence.NameHashKey] = "test-send-name-hash"
        });
    }

    internal static SubmitBindingProfile CreateVerifiedChatGptProfile(
        string submitBinding = "Ctrl+Enter",
        string newlineBinding = "Enter")
    {
        return SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            submitBinding,
            newlineBinding,
            CreateVerifiedChatGptDiscovery());
    }

    internal static TextSurfaceDescriptor CreateNativeSubmitSurface(string profileId)
    {
        return TestSurfaceFactory.CreateNativeSubmitSurface(profileId);
    }

    protected static OsInteractionResult CreateSubmittedResult(string profileId)
    {
        return new OsInteractionResult(
            OsInteractionStatusIds.Submitted,
            CreateNativeSubmitSurface(profileId),
            null,
            null,
            Applied: true,
            Submitted: true,
            Diagnostics: new Dictionary<string, string> { ["profile_id"] = profileId });
    }

    [Test]
    public void ProtectedSendTrace_RejectsSkippedDuplicateAndStaleTransitions()
    {
        var trace = Array.Empty<ProtectedSendTraceEntry>();
        const long attemptId = 7;
        const long generation = 3;
        const string fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.That(ProtectedSendTrace.TryAppend(
            trace,
            attemptId,
            generation,
            fingerprint,
            "send_detected",
            "test.secret.com",
            0,
            out _), Is.False);
        Assert.That(ProtectedSendTrace.TryAppend(
            trace,
            attemptId,
            generation,
            "test.secret.com",
            "send_detected",
            "checking_prompt",
            0,
            out _), Is.False);
        Assert.That(ProtectedSendTrace.TryAppend(
            trace,
            attemptId,
            generation,
            fingerprint,
            "send_detected",
            "checking_prompt",
            0,
            out var detected), Is.True);
        Assert.That(ProtectedSendTrace.TryAppend(
            detected,
            attemptId,
            generation,
            fingerprint,
            "sanitized",
            "sanitization_verified",
            2,
            out _), Is.False);
        Assert.That(ProtectedSendTrace.TryAppend(
            detected,
            attemptId,
            generation,
            fingerprint,
            "target_matched",
            "target_verified",
            3,
            out var matched), Is.True);
        Assert.That(ProtectedSendTrace.TryAppend(
            matched,
            attemptId,
            generation,
            fingerprint,
            "target_matched",
            "target_verified",
            3,
            out _), Is.False);
        Assert.That(ProtectedSendTrace.TryAppend(
            matched,
            attemptId + 1,
            generation,
            fingerprint,
            "composer_read",
            "capture_verified",
            4,
            out _), Is.False);
        Assert.That(matched.All(entry => entry.TargetFingerprint == fingerprint), Is.True);
    }

    [Test]
    public void ProtectedSendTrace_TypedTransitionContractRejectsUnknownStageAndUnsafeResult()
    {
        Assert.That(
            ProtectedSendTraceTransition.TryCreate(
                "unknown_stage",
                "capture_verified",
                out _),
            Is.False);
        Assert.That(
            ProtectedSendTraceTransition.TryCreate(
                "composer_read",
                "raw value",
                out _),
            Is.False);
        Assert.That(
            ProtectedSendTraceTransition.TryCreate(
                "sent_safely",
                "capture_verified",
                out _),
            Is.False);

        Assert.That(
            ProtectedSendTrace.TryAppend(
                Array.Empty<ProtectedSendTraceEntry>(),
                attemptId: 7,
                snapshotGeneration: 3,
                targetFingerprint: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                transition: default,
                durationMilliseconds: 0,
                out _),
            Is.False);

        Assert.That(
            ProtectedSendTraceTransition.TryCreate(
                "send_detected",
                "checking_prompt",
                out var transition),
            Is.True);
        Assert.That(transition.Stage, Is.EqualTo(ProtectedSendTraceStage.SendDetected));
        Assert.That(transition.StageToken, Is.EqualTo("send_detected"));
        Assert.That(transition.ResultCode.Value, Is.EqualTo("checking_prompt"));
        Assert.That(
            ProtectedSendTraceTransition.TryCreate(
                "terminal_blocked",
                OsInteractionStatusIds.Submitted,
                out _),
            Is.False);
    }

    [Test]
    public void ProtectedSendTrace_AllowsReplayIndeterminateTerminalOutcome()
    {
        const string fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var trace = Array.Empty<ProtectedSendTraceEntry>();
        Assert.That(ProtectedSendTrace.TryAppend(
            trace,
            7,
            3,
            fingerprint,
            "send_detected",
            "checking_prompt",
            0,
            out var detected), Is.True);
        Assert.That(ProtectedSendTrace.TryAppend(
            detected,
            7,
            3,
            fingerprint,
            "target_matched",
            "target_verified",
            0,
            out var matched), Is.True);
        Assert.That(ProtectedSendTrace.TryAppend(
            matched,
            7,
            3,
            fingerprint,
            "terminal_blocked",
            OsInteractionStatusIds.ReplayIndeterminate,
            0,
            out var terminal), Is.True);
        Assert.That(terminal[^1].ResultCode, Is.EqualTo(OsInteractionStatusIds.ReplayIndeterminate));
    }

    [Test]
    public void ProtectedSendTrace_TypedAppendKeepsStoredTraceRawFree()
    {
        const string fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.That(
            ProtectedSendTraceTransition.TryCreate(
                "send_detected",
                "checking_prompt",
                out var transition),
            Is.True);

        Assert.That(
            ProtectedSendTrace.TryAppend(
                Array.Empty<ProtectedSendTraceEntry>(),
                attemptId: 7,
                snapshotGeneration: 3,
                fingerprint,
                transition,
                durationMilliseconds: 0,
                out var trace),
            Is.True);
        Assert.That(trace.Single().Stage, Is.EqualTo("send_detected"));
        Assert.That(trace.Single().ResultCode, Is.EqualTo("checking_prompt"));
        Assert.That(trace.Single().ResultCode, Does.Not.Contain("raw"));
    }

    [Test]
    public void ResidentOverlayDispatchQueue_SerializesAttemptsOnOneResidentThread()
    {
        var firstEntered = new ManualResetEventSlim(false);
        var releaseFirst = new ManualResetEventSlim(false);
        var order = new List<string>();
        var threadIds = new List<int>();
        var active = 0;
        var maxActive = 0;

        using var queue = new ResidentOverlayDispatchQueue(
            (model, _) =>
            {
                lock (order)
                {
                    order.Add(model.SanitizedPrompt);
                    threadIds.Add(Environment.CurrentManagedThreadId);
                    active++;
                    maxActive = Math.Max(maxActive, active);
                }

                if (model.SanitizedPrompt == "first")
                {
                    firstEntered.Set();
                    releaseFirst.Wait();
                }

                lock (order)
                {
                    active--;
                }

                return ConfirmationDecisionContract.Confirm(model);
            },
            cancelActive: static () => { });

        ConfirmationDecision? firstDecision = null;
        ConfirmationDecision? secondDecision = null;
        var firstThread = new Thread(() => firstDecision = queue.Request(CreateOverlayModel("first"), static (_, _) => true));
        var secondThread = new Thread(() => secondDecision = queue.Request(CreateOverlayModel("second"), static (_, _) => true));

        firstThread.Start();
        firstEntered.Wait();
        secondThread.Start();
        releaseFirst.Set();
        firstThread.Join();
        secondThread.Join();

        Assert.That(firstDecision?.Approved, Is.True);
        Assert.That(secondDecision?.Approved, Is.True);
        Assert.That(order, Is.EqualTo(new[] { "first", "second" }));
        Assert.That(threadIds.Distinct().Count(), Is.EqualTo(1));
        Assert.That(threadIds[0], Is.EqualTo(queue.UiThreadId));
        Assert.That(maxActive, Is.EqualTo(1));
    }

    [Test]
    public void ResidentOverlayDispatchQueue_DisposalCancelsActiveAttempt()
    {
        var handlerEntered = new ManualResetEventSlim(false);
        var releaseHandler = new ManualResetEventSlim(false);
        var cancelRequested = 0;
        ResidentOverlayDispatchQueue? queue = null;
        ConfirmationDecision? decision = null;
        var requestThread = new Thread(() =>
        {
            decision = queue!.Request(CreateOverlayModel("active"), static (_, _) => true);
        });

        queue = new ResidentOverlayDispatchQueue(
            (model, _) =>
            {
                handlerEntered.Set();
                releaseHandler.Wait();
                return Volatile.Read(ref cancelRequested) == 1
                    ? ConfirmationDecisionContract.Cancel(model)
                    : ConfirmationDecisionContract.Confirm(model);
            },
            cancelActive: () =>
            {
                Interlocked.Exchange(ref cancelRequested, 1);
                releaseHandler.Set();
            });

        requestThread.Start();
        handlerEntered.Wait();
        queue.Dispose();
        requestThread.Join();

        Assert.That(decision?.Approved, Is.False);
    }

    [Test]
    public void ProtectedSendTrace_TargetFingerprintIsOpaqueAndRawFree()
    {
        var identity = new NativeSubmitTargetIdentity(4, "chatgpt-desktop", "ABC123");

        var fingerprint = ProtectedSendTrace.TargetFingerprint(identity);

        Assert.That(fingerprint, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(fingerprint, Does.Not.Contain(identity.ProfileId));
        Assert.That(fingerprint, Does.Not.Contain(identity.WindowHandle));
    }

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
    public void WindowsNativeSubmitHookHost_DeferredGuardedClassificationDeliversExactlyOnce()
    {
        using var releaseClassification = new ManualResetEventSlim(false);
        using var delivered = new ManualResetEventSlim(false);
        var deliveries = 0;
        var host = new WindowsNativeSubmitHookHost();

        var immediate = host.ClassifyWithinBudgetForTest(
            () =>
            {
                releaseClassification.Wait();
                return new NativeSubmitInterceptionResult(
                    OsInteractionStatusIds.NativeSubmitGuarded,
                    SuppressOriginalInput: true,
                    Applied: false,
                    Submitted: false,
                    Diagnostics: new Dictionary<string, string>());
            },
            shouldSuppressFailure: () => true,
            onDeferredClassification: _ =>
            {
                Interlocked.Increment(ref deliveries);
                delivered.Set();
            },
            classificationBudget: TimeSpan.Zero);

        Assert.That(immediate.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(immediate.SuppressOriginalInput, Is.True);

        releaseClassification.Set();

        Assert.That(delivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(deliveries, Is.EqualTo(1));
    }

    [Test]
    public void NativeSubmitTargetIdentity_RejectsDelayedClassificationForDifferentWindow()
    {
        var surface = CreateNativeSubmitSurface("codex-desktop") with
        {
            Metadata = new SurfaceMetadata(
                ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                WindowHandle: "2")
        };

        var target = NativeSubmitTargetIdentity.TryCreateForGesture(
            snapshotGeneration: 1,
            surface: surface,
            gestureTargetWindow: new IntPtr(1));

        Assert.That(target, Is.Null);
    }

    [Test]
    public void NativeSubmitTargetIdentity_NormalizesPointerChildWindowToRootWindow()
    {
        var surface = CreateNativeSubmitSurface("codex-desktop") with
        {
            Metadata = new SurfaceMetadata(WindowHandle: "2A")
        };

        var target = NativeSubmitTargetIdentity.TryCreateForGesture(
            snapshotGeneration: 1,
            surface,
            gestureTargetWindow: new IntPtr(0x2B),
            rootWindowResolver: _ => new IntPtr(0x2A));

        Assert.That(target, Is.Not.Null);
        Assert.That(target!.WindowHandle, Is.EqualTo("2A"));
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
    public void ReferenceOnlyInputSource_RejectsSelectedAndPersistedProfiles()
    {
        var target = ReferenceOnlyInputTarget.ForCurrentProcessForTest(new IntPtr(42));

        Assert.That(ReferenceOnlyInputSource.TryCreateForAcceptance(
            "codex-desktop",
            target,
            (_, _) => ReferenceOnlyInputDispatchResult.Unavailable,
            _ => { },
            out var codex), Is.False);
        Assert.That(codex, Is.Null);
        Assert.That(ReferenceOnlyInputSource.TryCreateForAcceptance(
            "chatgpt-desktop",
            target,
            (_, _) => ReferenceOnlyInputDispatchResult.Unavailable,
            _ => { },
            out var chatGpt), Is.False);
        Assert.That(chatGpt, Is.Null);
        Assert.That(ReferenceOnlyInputSource.TryCreateForAcceptance(
            "custom-profile",
            target,
            (_, _) => ReferenceOnlyInputDispatchResult.Unavailable,
            _ => { },
            out var custom), Is.False);
        Assert.That(custom, Is.Null);

        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            ReferenceOnlyInputSource.ProfileId,
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(ReferenceOnlyInputSource.ProfileId)));

        Assert.That(profile.IsProtected, Is.False);
        Assert.That(profile.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.ReferenceSourceUnavailable));

        var directory = Path.Combine(Path.GetTempPath(), "CodexRedactionGate", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(directory);
            var stored = SubmitBindingProfileStore.Save(
                layout,
                new[] { profile });

            Assert.That(stored.Succeeded, Is.False);
            Assert.That(stored.Code, Is.EqualTo("reference_profile_forbidden"));

            layout.EnsureDirectories();
            File.WriteAllText(
                SubmitBindingProfileStore.DefaultPath(layout),
                "{\"Profiles\":[{\"profile_id\":\"reference-composer\",\"enabled\":true}]}");
            var loaded = SubmitBindingProfileStore.Load(layout);

            Assert.That(loaded.Succeeded, Is.False);
            Assert.That(loaded.Code, Is.EqualTo("reference_profile_forbidden"));
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
    public void ReferenceOnlyInputSource_ExpiresBeforeItCanDispatch()
    {
        var dispatches = 0;
        var revocations = 0;
        var target = ReferenceOnlyInputTarget.ForCurrentProcessForTest(new IntPtr(42));
        Assert.That(ReferenceOnlyInputSource.TryCreateForAcceptance(
            ReferenceOnlyInputSource.ProfileId,
            target,
            (_, _) =>
            {
                Interlocked.Increment(ref dispatches);
                return new ReferenceOnlyInputDispatchResult(true, true, OsInteractionStatusIds.NativeSubmitGuarded);
            },
            _ => Interlocked.Increment(ref revocations),
            TimeSpan.Zero,
            out var source), Is.True);

        using (source)
        {
            var result = source!.DispatchKeyboard(new NativeKeyGesture(
                "Enter",
                TargetWindow: target.RootWindow,
                TargetProcessId: target.ProcessId));

            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.ReferenceSourceUnavailable));
        }

        Assert.That(dispatches, Is.Zero);
        Assert.That(revocations, Is.EqualTo(1));
    }

    [Test]
    public void ReferenceOnlyInputSource_UsesHookOwnedKeyboardAndPointerRoutesAndRevokesOnDispose()
    {
        using var keyboardDelivered = new ManualResetEventSlim(false);
        using var pointerDelivered = new ManualResetEventSlim(false);
        var keyboardClassifications = 0;
        var pointerClassifications = 0;
        var host = new WindowsNativeSubmitHookHost();
        host.ConfigureReferenceSourceCallbacksForTest(
            gesture =>
            {
                Interlocked.Increment(ref keyboardClassifications);
                return GuardedResult();
            },
            (_, _) => keyboardDelivered.Set(),
            _ => true,
            gesture =>
            {
                Interlocked.Increment(ref pointerClassifications);
                return GuardedResult();
            },
            (_, _) => pointerDelivered.Set(),
            _ => true);

        var targetWindow = new IntPtr(42);
        Assert.That(
            () => host.OpenReferenceOnlyInputSourceForAcceptance(targetWindow),
            Throws.TypeOf<InvalidOperationException>());
        using var source = host.OpenReferenceOnlyInputSourceForTest(targetWindow);
        var keyboard = source.DispatchKeyboard(new NativeKeyGesture(
            "Enter",
            TargetWindow: targetWindow,
            TargetProcessId: (uint)Environment.ProcessId));
        var pointer = source.DispatchPointer(new NativePointerGesture(
            10,
            10,
            "left",
            targetWindow,
            (uint)Environment.ProcessId));

        Assert.That(keyboard.Accepted, Is.True);
        Assert.That(keyboard.SuppressOriginalInput, Is.True);
        Assert.That(pointer.Accepted, Is.True);
        Assert.That(pointer.SuppressOriginalInput, Is.True);
        Assert.That(keyboardDelivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(pointerDelivered.Wait(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(keyboardClassifications, Is.EqualTo(1));
        Assert.That(pointerClassifications, Is.EqualTo(1));

        var rightButton = source.DispatchPointer(new NativePointerGesture(
            10,
            10,
            "right",
            targetWindow,
            (uint)Environment.ProcessId));
        Assert.That(rightButton.Accepted, Is.True);
        Assert.That(rightButton.SuppressOriginalInput, Is.False);
        Assert.That(pointerClassifications, Is.EqualTo(1));

        var wrongProcess = source.DispatchKeyboard(new NativeKeyGesture(
            "Enter",
            TargetWindow: targetWindow,
            TargetProcessId: (uint)Environment.ProcessId + 1));
        Assert.That(wrongProcess.Accepted, Is.False);
        Assert.That(wrongProcess.Status, Is.EqualTo(OsInteractionStatusIds.ReferenceSourceUnavailable));

        ReferenceOnlyInputDispatchResult? wrongThread = null;
        var thread = new Thread(() =>
        {
            wrongThread = source.DispatchKeyboard(new NativeKeyGesture(
                "Enter",
                TargetWindow: targetWindow,
                TargetProcessId: (uint)Environment.ProcessId));
        });
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(wrongThread, Is.Not.Null);
        Assert.That(wrongThread!.Status, Is.EqualTo(OsInteractionStatusIds.ReferenceSourceUnavailable));

        source.Dispose();
        var revoked = source.DispatchKeyboard(new NativeKeyGesture(
            "Enter",
            TargetWindow: targetWindow,
            TargetProcessId: (uint)Environment.ProcessId));
        Assert.That(revoked.Accepted, Is.False);
        Assert.That(revoked.Status, Is.EqualTo(OsInteractionStatusIds.ReferenceSourceUnavailable));

        using var replacement = host.OpenReferenceOnlyInputSourceForTest(targetWindow);
        var replacementDispatch = replacement.DispatchKeyboard(new NativeKeyGesture(
            "Enter",
            TargetWindow: targetWindow,
            TargetProcessId: (uint)Environment.ProcessId));
        Assert.That(replacementDispatch.Accepted, Is.True);
    }

    private static NativeSubmitInterceptionResult GuardedResult()
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.NativeSubmitGuarded,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>());
    }

    [Test]
    public void WindowsNativeSubmitHookHost_UsesResidentTargetAndBindingBeforeSuppressing()
    {
        var profile = CreateProtectedProfile() with
        {
            ProfileId = "chatgpt-desktop",
            SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding!
        };
        var host = new WindowsNativeSubmitHookHost(
            new[] { profile },
            _ => "chatgpt-desktop");
        var target = new IntPtr(42);
        host.RefreshSelectedTargetForTest(target, 7, "chatgpt-desktop");

        Assert.That(host.IsPotentialKeyboardInterceptionForTest(new NativeKeyGesture("A", TargetWindow: target, TargetProcessId: 7), 0x41), Is.False);
        Assert.That(host.IsPotentialKeyboardInterceptionForTest(new NativeKeyGesture("Enter", TargetWindow: target, TargetProcessId: 7), 0x0d), Is.False);
        Assert.That(host.IsPotentialKeyboardInterceptionForTest(new NativeKeyGesture("Enter", Ctrl: true, TargetWindow: target, TargetProcessId: 7), 0x0d), Is.True);
    }

    [Test]
    public void WindowsNativeSubmitHookHost_StillClassifiesEnterForAnUnconfiguredSelectedApp()
    {
        var host = new WindowsNativeSubmitHookHost(
            new[] { FirstRunSetupController.CreateDefaultSetupProfile("codex-desktop")! },
            _ => "codex-desktop");
        var target = new IntPtr(42);
        host.RefreshSelectedTargetForTest(target, 7, "codex-desktop");

        Assert.That(host.IsPotentialKeyboardInterceptionForTest(new NativeKeyGesture("A", TargetWindow: target, TargetProcessId: 7), 0x41), Is.False);
        Assert.That(host.IsPotentialKeyboardInterceptionForTest(new NativeKeyGesture("Enter", TargetWindow: target, TargetProcessId: 7), 0x0d), Is.True);
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
        Assert.That(inner.LastSurface!.Metadata.TryGetValue("submit_binding"), Is.EqualTo("Ctrl+Enter"));
        Assert.That(inner.LastSurface.Metadata.TryGetValue("submit_binding_sendkeys"), Is.EqualTo("^{ENTER}"));
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
        Assert.That(unhealthy.Status, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
        Assert.That(unhealthy.SuppressOriginalInput, Is.True);
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
        var attemptStatuses = new List<string>();
        var activeSurfaceDiscoveryCalls = 0;
        Func<TextSurfaceDiscoveryResult> activeSurfaceDiscovery = () =>
        {
            activeSurfaceDiscoveryCalls++;
            return TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"));
        };
        var controller = TrayProtectionController.CreateTest(
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
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: activeSurfaceDiscovery),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                CreateNativeSubmitSurface("codex-desktop"),
                null,
                null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }),
            profile,
            activeSurfaceDiscovery: activeSurfaceDiscovery);
        controller.StateChanged += (_, _) => attemptStatuses.Add(controller.State.ProtectedSendAttemptStatus);

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
        Assert.That(attemptStatuses, Does.Contain("detected"));
        Assert.That(attemptStatuses, Does.Contain("checking"));
        Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("sent_safely"));
        Assert.That(controller.State.ProtectedSendAttemptAction, Is.EqualTo("none"));
        Assert.That(activeSurfaceDiscoveryCalls, Is.EqualTo(1));
    }

    [Test]
    public void TrayProtectionController_DoesNotPublishProtectedSendAttemptForOrdinaryInput()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"))),
            () => throw new InvalidOperationException("Protected submit should not run."),
            profile,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));

        Assert.That(controller.Start(), Is.True);

        hook.Trigger(new NativeKeyGesture("A", Ctrl: true));

        Assert.That(hook.LastClassification!.SuppressOriginalInput, Is.False);
        Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("idle"));
        Assert.That(controller.State.ProtectedSendAttemptAction, Is.EqualTo("none"));
    }

    [Test]
    public void TrayProtectionController_PassesCtrlEnterThroughOutsideTheSelectedComposer()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitFlowCalls = 0;
        var unrelatedSurface = TextSurfaceDiscoveryResult.Failure(
            OsInteractionStatusIds.UnsupportedSurface,
            new Dictionary<string, string>());
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: () => unrelatedSurface),
            () =>
            {
                submitFlowCalls++;
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    null,
                    null,
                    null,
                    Applied: true,
                    Submitted: true,
                    Diagnostics: new Dictionary<string, string>());
            },
            profile,
            activeSurfaceDiscovery: () => unrelatedSurface);

        Assert.That(controller.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification!.SuppressOriginalInput, Is.False);
        Assert.That(submitFlowCalls, Is.EqualTo(0));
        Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("idle"));
    }

    [Test]
    public void TrayProtectionController_RunsSuppressedSubmitFlowForEveryProtectedSend()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitFlowCalls = 0;
        var controller = TrayProtectionController.CreateTest(
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
            var inProgressAttemptStatusSeen = false;
            controller = TrayProtectionController.CreateTest(
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
                        inProgressAttemptStatusSeen = controller.State.ProtectedSendAttemptStatus == "in_progress";
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
            Assert.That(inProgressAttemptStatusSeen, Is.True, flowStatus);
            Assert.That(controller.State.LastStatus, Is.EqualTo(flowStatus), flowStatus);
            Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected), flowStatus);
            Assert.That(controller.State.ComposerProtected, Is.True, flowStatus);
            Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo(
                flowStatus == OsInteractionStatusIds.Submitted ? "sent_safely" :
                flowStatus == OsInteractionStatusIds.Canceled ? "canceled" :
                flowStatus == OsInteractionStatusIds.Blocked ? "content_blocked" :
                "protection_unavailable"), flowStatus);
        }
    }

    [Test]
    public void TrayProtectionController_HandlesNextProtectedSendAfterCancel()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitFlowCalls = 0;
        var controller = TrayProtectionController.CreateTest(
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
        Assert.That(afterCancel.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "target_matched",
            "composer_read",
            "sanitized",
            "send_injected",
            "terminal_blocked"
        }));
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(controller.State.LastSubmitted, Is.True);
        Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
        Assert.That(controller.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "target_matched",
            "composer_read",
            "sanitized",
            "send_injected",
            "sent_safely"
        }));
        Assert.That(controller.State.ProtectedSendAttemptTrace!.All(entry =>
            entry.AttemptId == controller.State.ProtectedSendAttemptId
            && entry.SnapshotGeneration == controller.GetCurrentSnapshot().Generation
            && !string.IsNullOrWhiteSpace(entry.TargetFingerprint)
            && entry.DurationMilliseconds >= 0), Is.True);
    }

    [Test]
    public void TrayProtectionController_CompletesTheActiveAttemptAfterADuplicateProtectedSend()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        TrayProtectionController? controller = null;
        var submitFlowCalls = 0;
        var attemptIdBeforeDuplicate = 0L;
        controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                submitFlowCalls++;
                attemptIdBeforeDuplicate = controller!.State.ProtectedSendAttemptId;
                hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
                Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("in_progress"));
                Assert.That(controller.State.ProtectedSendAttemptId, Is.EqualTo(attemptIdBeforeDuplicate));
                return new OsInteractionResult(
                    OsInteractionStatusIds.Submitted,
                    CreateNativeSubmitSurface("codex-desktop"),
                    null,
                    null,
                    Applied: true,
                    Submitted: true,
                    Diagnostics: new Dictionary<string, string>());
            },
            profile);

        Assert.That(controller.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(submitFlowCalls, Is.EqualTo(1));
        Assert.That(attemptIdBeforeDuplicate, Is.GreaterThan(0));
        Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("sent_safely"));
        Assert.That(controller.State.ProtectedSendAttemptAction, Is.EqualTo("none"));
        Assert.That(controller.State.ProtectedSendAttemptId, Is.EqualTo(attemptIdBeforeDuplicate));
    }

    [Test]
    public void TrayProtectionController_RuntimeReloadInterruptsAnActiveAttemptWithARawFreeStatus()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var replacementHook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        Func<OsInteractionResult> submittedResult = () => new OsInteractionResult(
            OsInteractionStatusIds.Submitted,
            CreateNativeSubmitSurface(profile.ProfileId),
            null,
            null,
            Applied: true,
            Submitted: true,
            Diagnostics: new Dictionary<string, string>());
        TrayProtectionController? controller = null;
        var replacementRuntime = NativeSubmitRuntime.CreateTest(
            replacementHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            submittedResult,
            profile);
        controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                Assert.That(controller!.State.ProtectedSendAttemptStatus, Is.EqualTo("checking"));
                Assert.That(controller.ReloadNativeSubmit(replacementRuntime), Is.True);
                return submittedResult();
            },
            profile);

        Assert.That(controller.Start(), Is.True);
        oldHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("trace_unavailable"));
        Assert.That(controller.State.ProtectedSendAttemptAction, Is.EqualTo("retry_protection"));
        Assert.That(controller.State.ProtectedSendAttemptId, Is.GreaterThan(0));
        Assert.That(controller.State.ProtectedSendAttemptTrace, Is.Not.Null.Or.Empty);
        Assert.That(controller.State.ProtectedSendAttemptTrace!.Last().Stage, Is.EqualTo("terminal_blocked"));
        Assert.That(controller.State.ProtectedSendAttemptTrace!.Last().ResultCode, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
        Assert.That(controller.State.LastProtectedSendInterruption, Is.Not.Null);
        Assert.That(controller.State.LastProtectedSendInterruption!.AttemptId, Is.GreaterThan(0));
        Assert.That(controller.State.LastProtectedSendInterruption.SourceGeneration, Is.EqualTo(0));
        Assert.That(controller.State.LastProtectedSendInterruption.Reason, Is.EqualTo("runtime_replaced"));
        Assert.That(controller.State.LastProtectedSendInterruption.Action, Is.EqualTo("retry_protection"));

        replacementHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(controller.State.LastProtectedSendInterruption, Is.Null);
    }

    [Test]
    public void TrayProtectionController_StopCancelsResidentOperationBeforeSubmitSideEffect()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        TrayProtectionController? controller = null;
        var submitSideEffects = 0;

        OsInteractionResult FailedClosed()
        {
            return new OsInteractionResult(
                OsInteractionStatusIds.FailedClosed,
                CreateNativeSubmitSurface(profile.ProfileId),
                null,
                null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["trace_status"] = "resident_operation_unavailable"
                });
        }

        OsInteractionResult RunResident(Func<bool> executionGuard)
        {
            controller!.Stop();
            if (!executionGuard())
            {
                return FailedClosed();
            }

            submitSideEffects++;
            return new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                CreateNativeSubmitSurface(profile.ProfileId),
                null,
                null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string>());
        }

        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("The resident traced runner must be used."),
            profile,
            ResidentTracedRunner: (_, executionGuard, _) => RunResident(executionGuard),
            ResidentTargetTracedRunner: (_, _, executionGuard, _) => RunResident(executionGuard));

        controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(controller.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(submitSideEffects, Is.Zero);
        Assert.That(controller.State.Enabled, Is.False);
        Assert.That(controller.State.NativeSubmitEnabled, Is.False);
        Assert.That(hook.Started, Is.False);
    }

    [Test]
    public void TrayProtectionController_MissingResidentRunnerFailsClosedWithTraceUnavailable()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runtime = new NativeSubmitRuntime(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            profile,
            ResidentTracedRunner: null,
            ResidentTargetTracedRunner: null);
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(controller.Start(), Is.False);
        Assert.That(hook.Started, Is.False);
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(controller.State.LastProtectedSendTraceStatus, Is.EqualTo("trace_unavailable"));
    }

    [Test]
    public void TrayProtectionController_ClearsStaleTraceStatusAfterSuccessfulProtectedSend()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var nativeController = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(
                CreateNativeSubmitSurface(profile.ProfileId)));
        OsInteractionResult RunSubmit()
        {
            submitCalls++;
            return submitCalls == 1
                ? new OsInteractionResult(
                    OsInteractionStatusIds.FailedClosed,
                    CreateNativeSubmitSurface(profile.ProfileId),
                    null,
                    null,
                    Applied: false,
                    Submitted: false,
                    Diagnostics: new Dictionary<string, string>
                    {
                        ["profile_id"] = profile.ProfileId,
                        ["trace_status"] = "trace_unavailable"
                    })
                : CreateSubmittedResult(profile.ProfileId);
        }

        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            nativeController,
            RunSubmit,
            profile);
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(controller.Start(), Is.True);

        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(controller.State.LastProtectedSendTraceStatus, Is.EqualTo("trace_unavailable"));

        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(controller.State.LastProtectedSendTraceStatus, Is.EqualTo("none"));
    }

    [Test]
    public void WindowsTrayApp_ProductionRuntimeProvidesOnlyTargetAwareResidentTracedRunner()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var profile = CreateProtectedProfile();
            using var runtimeSet = WindowsTrayApp.CreateNativeSubmitRuntimeSet(
                TestSanitizers.Create(),
                layout,
                new[] { profile });

            Assert.That(runtimeSet, Is.Not.Null);
            Assert.That(runtimeSet!.Runtimes, Has.Count.EqualTo(1));
            var runtime = runtimeSet.Runtimes.Single();
            Assert.That(runtime.ResidentTracedRunner, Is.Null);
            Assert.That(runtime.ResidentTargetTracedRunner, Is.Not.Null);
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
    public void TrayProtectionController_RuntimeReloadInterruptsAtEveryProtectedSendStage()
    {
        foreach (var reloadStage in new[] { "detection", "checking", "overlay", "write", "replay" })
        {
            var oldHook = new FakeNativeSubmitHookHost();
            var replacementHook = new FakeNativeSubmitHookHost();
            var profile = CreateProtectedProfile();
            var oldSubmitSideEffects = 0;
            var oldWriteSideEffects = 0;
            var oldReplaySideEffects = 0;
            var replacementSubmitSideEffects = 0;
            var reloaded = false;
            TrayProtectionController? controller = null;

            OsInteractionResult SubmittedResult() => new(
                OsInteractionStatusIds.Submitted,
                CreateNativeSubmitSurface(profile.ProfileId),
                null,
                null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string>());

            OsInteractionResult CompleteWithoutSideEffect(
                Func<string, string, bool>? traceStage = null,
                Func<bool>? executionGuard = null,
                Func<IDisposable?>? executionLease = null,
                bool countSideEffect = false)
            {
                var stages = new[]
                {
                    (Stage: "composer_read", Code: "capture_verified"),
                    (Stage: "sanitized", Code: "sanitization_verified"),
                    (Stage: "overlay_created", Code: "confirmation_requested"),
                    (Stage: "overlay_foreground_confirmed", Code: "foreground_verified"),
                    (Stage: "approved", Code: "user_approved"),
                    (Stage: "text_written", Code: "write_verified"),
                    (Stage: "send_injected", Code: "submit_requested")
                };
                foreach (var stage in stages)
                {
                    if (traceStage is not null && !traceStage(stage.Stage, stage.Code))
                    {
                        return new OsInteractionResult(
                            OsInteractionStatusIds.FailedClosed,
                            CreateNativeSubmitSurface(profile.ProfileId),
                            null,
                            null,
                            Applied: false,
                            Submitted: false,
                            Diagnostics: new Dictionary<string, string>
                            {
                                ["trace_status"] = "trace_unavailable"
                            });
                    }

                    if (countSideEffect && stage.Stage is "text_written" or "send_injected")
                    {
                        if (executionGuard is not null && !executionGuard())
                        {
                            return new OsInteractionResult(
                                OsInteractionStatusIds.FailedClosed,
                                CreateNativeSubmitSurface(profile.ProfileId),
                                null,
                                null,
                                Applied: false,
                                Submitted: false,
                                Diagnostics: new Dictionary<string, string>
                                {
                                    ["trace_status"] = "resident_operation_unavailable"
                                });
                        }

                        var lease = executionLease?.Invoke();
                        if (executionLease is not null && lease is null)
                        {
                            return new OsInteractionResult(
                                OsInteractionStatusIds.FailedClosed,
                                CreateNativeSubmitSurface(profile.ProfileId),
                                null,
                                null,
                                Applied: false,
                                Submitted: false,
                                Diagnostics: new Dictionary<string, string>
                                {
                                    ["trace_status"] = "resident_operation_unavailable"
                                });
                        }

                        try
                        {
                            if (stage.Stage == "text_written")
                            {
                                oldWriteSideEffects++;
                            }

                            if (stage.Stage == "send_injected")
                            {
                                oldReplaySideEffects++;
                            }
                        }
                        finally
                        {
                            lease?.Dispose();
                        }
                    }
                }

                if (countSideEffect)
                {
                    oldSubmitSideEffects++;
                }

                return SubmittedResult();
            }

            var replacementRuntime = NativeSubmitRuntime.CreateTest(
                replacementHook,
                new NativeSubmitInterceptionController(
                    profile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () =>
                {
                    replacementSubmitSideEffects++;
                    return SubmittedResult();
                },
                profile);

            var oldRuntime = NativeSubmitRuntime.CreateTest(
                oldHook,
                new NativeSubmitInterceptionController(
                    profile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () => CompleteWithoutSideEffect(countSideEffect: true),
                profile,
                ResidentTracedRunner: (traceStage, executionGuard, executionLease) =>
                    CompleteWithoutSideEffect(traceStage, executionGuard, executionLease, countSideEffect: true),
                ResidentTargetTracedRunner: (target, traceStage, executionGuard, executionLease) =>
                    CompleteWithoutSideEffect(traceStage, executionGuard, executionLease, countSideEffect: true));

            controller = TrayProtectionController.CreateTest(
                new FakeTrayHotkeyHost(),
                () => throw new InvalidOperationException("Manual scan should not run."),
                oldHook,
                oldRuntime.Controller,
                profile,
                protectedSendStageObserver: stage =>
                {
                    if (stage == reloadStage && !reloaded)
                    {
                        reloaded = true;
                        Assert.That(controller!.ReloadNativeSubmit(replacementRuntime), Is.True, reloadStage);
                    }
                },
                nativeSubmitRuntimes: new[] { oldRuntime });

            Assert.That(controller.Start(), Is.True, reloadStage);
            oldHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

            Assert.That(reloaded, Is.True, reloadStage);
            Assert.That(oldSubmitSideEffects, Is.Zero, reloadStage);
            Assert.That(
                oldWriteSideEffects,
                Is.EqualTo(reloadStage == "replay" ? 1 : 0),
                reloadStage);
            Assert.That(oldReplaySideEffects, Is.Zero, reloadStage);
            Assert.That(replacementSubmitSideEffects, Is.Zero, reloadStage);
            Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("trace_unavailable"), reloadStage);
            Assert.That(controller.State.ProtectedSendAttemptId, Is.GreaterThan(0), reloadStage);
            Assert.That(controller.State.ProtectedSendAttemptTrace, Is.Not.Null.Or.Empty, reloadStage);
            Assert.That(controller.State.ProtectedSendAttemptTrace!.Last().Stage, Is.EqualTo("terminal_blocked"), reloadStage);
            Assert.That(controller.State.ProtectedSendAttemptTrace!.Last().ResultCode, Is.EqualTo(OsInteractionStatusIds.FailedClosed), reloadStage);
            Assert.That(controller.State.LastProtectedSendInterruption, Is.Not.Null, reloadStage);
            Assert.That(controller.State.LastProtectedSendInterruption!.Reason, Is.EqualTo("runtime_replaced"), reloadStage);
            Assert.That(controller.State.LastProtectedSendInterruption.Action, Is.EqualTo("retry_protection"), reloadStage);
            Assert.That(controller.State.LastProtectedSendInterruption.AttemptId, Is.GreaterThan(0), reloadStage);
            Assert.That(controller.State.LastProtectedSendInterruption.SourceGeneration, Is.GreaterThanOrEqualTo(0), reloadStage);

            replacementHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
            Assert.That(controller.State.LastProtectedSendInterruption, Is.Null, reloadStage);
        }
    }

    [Test]
    public void ResidentProtectedSendOperation_CancellationWaitsForLifecycleLease()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("Test runner should not run."),
            profile);
        using var runtimeSet = new NativeSubmitRuntimeSet(hook, new[] { runtime });
        var snapshot = new ProtectionSnapshot(
            Generation: 3,
            State: new TrayProtectionState(
                Enabled: true,
                Mode: "NativeSubmit",
                Hotkey: "Ctrl+Shift+F9",
                LastStatus: OsInteractionStatusIds.NativeSubmitInProgress,
                LastDecision: null,
                LastReplacementCount: null,
                LastProfileId: profile.ProfileId,
                LastApplied: false,
                LastSubmitted: false),
            ApplyOnlyRunner: () => throw new InvalidOperationException("Apply-only runner should not run."),
            RuntimeSet: runtimeSet,
            HookReady: true,
            SendControlDiscovery: null,
            ActiveSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)));
        var sideEffectsCancelled = false;
        using var operation = new ResidentProtectedSendOperation(
            snapshot,
            runtimeSet,
            target: null,
            cancelSideEffects: () => sideEffectsCancelled = true);
        using var lease = operation.TryAcquireSideEffect(snapshot);
        Assert.That(lease, Is.Not.Null);

        using var cancellationStarted = new ManualResetEventSlim(false);
        using var cancellationFinished = new ManualResetEventSlim(false);
        var cancellationThread = new Thread(() =>
        {
            cancellationStarted.Set();
            operation.Cancel();
            cancellationFinished.Set();
        });
        cancellationThread.Start();

        Assert.That(cancellationStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(SpinWait.SpinUntil(() => operation.IsCancelled, TimeSpan.FromSeconds(1)), Is.True);
        Assert.That(cancellationFinished.IsSet, Is.False);
        lease!.Dispose();
        Assert.That(cancellationFinished.Wait(TimeSpan.FromSeconds(1)), Is.True);
        cancellationThread.Join();
        Assert.That(operation.IsCancelled, Is.True);
        Assert.That(sideEffectsCancelled, Is.True);
    }

    [Test]
    public void ResidentProtectedSendOperation_TransactionalTraceAppendDoesNotCommitWhenPublicationIsInvalidated()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("Test runner should not run."),
            profile);
        using var runtimeSet = new NativeSubmitRuntimeSet(hook, new[] { runtime });
        var snapshot = new ProtectionSnapshot(
            Generation: 3,
            State: new TrayProtectionState(
                Enabled: true,
                Mode: "NativeSubmit",
                Hotkey: "Ctrl+Shift+F9",
                LastStatus: OsInteractionStatusIds.NativeSubmitInProgress,
                LastDecision: null,
                LastReplacementCount: null,
                LastProfileId: profile.ProfileId,
                LastApplied: false,
                LastSubmitted: false),
            ApplyOnlyRunner: () => throw new InvalidOperationException("Apply-only runner should not run."),
            RuntimeSet: runtimeSet,
            HookReady: true,
            SendControlDiscovery: null,
            ActiveSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)));
        using var operation = new ResidentProtectedSendOperation(snapshot, runtimeSet, target: null);

        var published = false;
        Assert.That(operation.TryAppendTraceTransaction(
            "send_detected",
            "checking_prompt",
            0,
            _ => published = false,
            out var trace), Is.False);

        Assert.That(published, Is.False);
        Assert.That(trace, Is.Empty);
        Assert.That(operation.Trace, Is.Empty);

        IReadOnlyList<ProtectedSendTraceEntry>? terminalPublication = null;
        Assert.That(operation.TryEnsureTerminalBlockedTraceTransaction(
            candidate =>
            {
                terminalPublication = candidate;
                return true;
            },
            out var terminalTrace), Is.True);

        Assert.That(terminalPublication, Is.EqualTo(terminalTrace));
        Assert.That(terminalTrace.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "terminal_blocked"
        }));
    }

    [Test]
    public void ResidentProtectedSendOperation_TransactionalTraceAppendCommitsOnlyThePublishedTrace()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("Test runner should not run."),
            profile);
        using var runtimeSet = new NativeSubmitRuntimeSet(hook, new[] { runtime });
        var snapshot = new ProtectionSnapshot(
            Generation: 3,
            State: new TrayProtectionState(
                Enabled: true,
                Mode: "NativeSubmit",
                Hotkey: "Ctrl+Shift+F9",
                LastStatus: OsInteractionStatusIds.NativeSubmitInProgress,
                LastDecision: null,
                LastReplacementCount: null,
                LastProfileId: profile.ProfileId,
                LastApplied: false,
                LastSubmitted: false),
            ApplyOnlyRunner: () => throw new InvalidOperationException("Apply-only runner should not run."),
            RuntimeSet: runtimeSet,
            HookReady: true,
            SendControlDiscovery: null,
            ActiveSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)));
        using var operation = new ResidentProtectedSendOperation(snapshot, runtimeSet, target: null);

        IReadOnlyList<ProtectedSendTraceEntry>? publishedTrace = null;
        Assert.That(operation.TryAppendTraceTransaction(
            "send_detected",
            "checking_prompt",
            0,
            candidate =>
            {
                publishedTrace = candidate;
                return true;
            },
            out var trace), Is.True);

        Assert.That(publishedTrace, Is.EqualTo(trace));
        Assert.That(operation.Trace, Is.EqualTo(trace));
        Assert.That(trace.Select(entry => entry.Stage), Is.EqualTo(new[] { "send_detected" }));
    }

    private static ConfirmationUiModel CreateOverlayModel(string prompt)
    {
        return new ConfirmationUiModel(
            prompt,
            Array.Empty<HighlightedReplacementSpan>(),
            new Dictionary<string, int>(),
            Array.Empty<string>(),
            "Confirm",
            "Cancel",
            RawValuesVisible: false);
    }

    [Test]
    public void TrayProtectionController_PublishesDistinctTerminalProtectedSendAttemptStatuses()
    {
        foreach (var (flowStatus, expectedAttemptStatus) in new[]
        {
            (OsInteractionStatusIds.FocusLost, "composer_changed"),
            (OsInteractionStatusIds.StaleComposer, "composer_changed"),
            (OsInteractionStatusIds.SurfaceUnverified, "binding_not_verified"),
            (OsInteractionStatusIds.NativeSubmitSetupRequired, "setup_required"),
            (OsInteractionStatusIds.CaptureFailed, "capture_failed"),
            (OsInteractionStatusIds.WriteFailed, "write_failed"),
            (OsInteractionStatusIds.VerificationFailed, "verification_failed"),
            (OsInteractionStatusIds.SubmitFailed, "submit_failed"),
            (OsInteractionStatusIds.ReplayIndeterminate, "replay_indeterminate"),
            (OsInteractionStatusIds.FailedClosed, "protection_unavailable"),
            (OsInteractionStatusIds.TraceUnavailable, "trace_unavailable"),
            (OsInteractionStatusIds.EnterpriseBlocked, "policy_blocked"),
            (OsInteractionStatusIds.BindingUnknown, "binding_not_verified"),
            (OsInteractionStatusIds.NotConfigured, "binding_not_verified")
        })
        {
            var hook = new FakeNativeSubmitHookHost();
            var profile = CreateProtectedProfile();
            var controller = TrayProtectionController.CreateTest(
                new FakeTrayHotkeyHost(),
                () => throw new InvalidOperationException("Manual scan should not run."),
                hook,
                new NativeSubmitInterceptionController(
                    profile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () => new OsInteractionResult(
                    flowStatus,
                    CreateNativeSubmitSurface("codex-desktop"),
                    null,
                    null,
                    Applied: false,
                    Submitted: false,
                    Diagnostics: new Dictionary<string, string>()),
                profile);

            Assert.That(controller.Start(), Is.True, flowStatus);
            hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

            Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo(expectedAttemptStatus), flowStatus);
            Assert.That(controller.State.ProtectedSendAttemptStatus, Is.Not.EqualTo("sent_safely"), flowStatus);
            if (flowStatus == OsInteractionStatusIds.TraceUnavailable)
            {
                Assert.That(controller.State.ReadinessStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
            }
        }
    }

    [Test]
    public void TrayProtectionController_DoesNotClaimProtectedWhenTraceRunnerFails()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => new OsInteractionResult(
                OsInteractionStatusIds.FailedClosed,
                CreateNativeSubmitSurface(profile.ProfileId),
                null,
                null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["trace_status"] = "send_injected_unavailable"
                }),
            profile);

        Assert.That(controller.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(controller.State.ComposerProtected, Is.False);
        Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("trace_unavailable"));
    }

    [Test]
    public void TrayProtectionController_CrashIsCapturedByOrchestratorNotController()
    {
        // The crash boundary should be at OsInteractionOrchestrator.RunOnce,
        // not in TrayProtectionController.RunNativeSubmitOnce

        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();

        // Create a submit runner that throws an exception
        var exceptionThrown = false;
        var controller = TrayProtectionController.CreateTest(
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
            () =>
            {
                exceptionThrown = true;
                throw new InvalidOperationException("Test exception from orchestrator boundary");
            });

        var started = controller.Start();
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        // Exception should be caught by OsInteractionOrchestrator, not TrayProtectionController
        Assert.That(started, Is.True);
        Assert.That(exceptionThrown, Is.True);
        // The result should be FailedClosed from orchestrator, not NativeSubmitCrashed from controller
        // Check what diagnostics are present to understand where exception was caught
        Assert.That(controller.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.FailedClosed),
            $"Expected FailedClosed but got {controller.State.LastStatus}. Diagnostics: {string.Join(", ", controller.State.LastStatus)}");
        Assert.That(controller.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
    }

    [Test]
    public void SubmitBindingOnboardingVerifier_SupportsBothEnterAndCtrlEnterPairs()
    {
        // Test that both supported pairs are valid:
        // 1. Enter Send / Ctrl+Enter newline
        // 2. Ctrl+Enter Send / Enter newline

        var profileId = "codex-desktop";
        var surface = CreateNativeSubmitSurface(profileId);
        var discovery = TextSurfaceDiscoveryResult.Success(surface);

        // Pair 1: Enter as Send, Ctrl+Enter as newline
        var pair1 = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId,
            "Enter",
            "Ctrl+Enter",
            discovery);

        Assert.That(pair1.IsProtected, Is.True);
        Assert.That(pair1.BindingSource, Is.EqualTo("user_verified"));
        Assert.That(pair1.SubmitBinding?.DisplayText, Is.EqualTo("Enter"));
        Assert.That(pair1.NewlineBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));

        // Pair 2: Ctrl+Enter as Send, Enter as newline
        var pair2 = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId,
            "Ctrl+Enter",
            "Enter",
            discovery);

        Assert.That(pair2.IsProtected, Is.True);
        Assert.That(pair2.BindingSource, Is.EqualTo("user_verified"));
        Assert.That(pair2.SubmitBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));
        Assert.That(pair2.NewlineBinding?.DisplayText, Is.EqualTo("Enter"));
    }

    [Test]
    public void SubmitBindingOnboardingVerifier_RejectsSameBindingForSubmitAndNewline()
    {
        var profileId = "codex-desktop";
        var surface = CreateNativeSubmitSurface(profileId);
        var discovery = TextSurfaceDiscoveryResult.Success(surface);

        // Same binding for both should fail
        var result = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId,
            "Enter",
            "Enter",
            discovery);

        Assert.That(result.IsProtected, Is.False);
        Assert.That(result.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.BindingUnknown));
        Assert.That(result.Diagnostics["binding_error"], Is.EqualTo("submit_newline_same_binding"));
    }

    [Test]
    public void SubmitBindingOnboardingVerifier_FailsWhenDiscoveringUnsupportedSurface()
    {
        var profileId = "codex-desktop";
        var surface = CreateNativeSubmitSurface(profileId);
        var discovery = TextSurfaceDiscoveryResult.Failure(
            OsInteractionStatusIds.UnsupportedSurface,
            new Dictionary<string, string> { ["unsupported_scope"] = "browser" });

        var result = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId,
            "Enter",
            "Ctrl+Enter",
            discovery);

        Assert.That(result.IsProtected, Is.False);
        Assert.That(result.CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(result.Diagnostics["surface_status"], Is.EqualTo(OsInteractionStatusIds.UnsupportedSurface));
    }

    [Test]
    public void SubmitBindingOnboardingVerifier_PersistsAndReloadsBindings()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
                "codex-desktop",
                "Enter",
                "Ctrl+Enter",
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));

            var save = SubmitBindingProfileStore.Upsert(layout, profile);
            var load = SubmitBindingProfileStore.Load(layout);

            Assert.That(save.Succeeded, Is.True);
            Assert.That(load.Succeeded, Is.True);
            Assert.That(load.Profiles, Has.Count.EqualTo(1));
            Assert.That(load.Profiles[0].SubmitBinding?.DisplayText, Is.EqualTo("Enter"));
            Assert.That(load.Profiles[0].NewlineBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));
            Assert.That(load.Profiles[0].CapabilityStatus, Is.EqualTo(OsInteractionStatusIds.Protected));
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
            var stored = SubmitBindingProfileStore.Load(layout).Profiles.Single(profile => profile.ProfileId == "codex-desktop");

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
    public void NativeSubmitProductSmokeRunner_UsesPersistedSubmitAndNewlineBindings()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            // Create and save profile with specific bindings
            var surface = TestSurfaceFactory.UpdateSurface(
                CreateNativeSubmitSurface("codex-desktop"),
                surfaceKind: "disposable_local_target",
                cloudSubmission: "false",
                composerStatus: null);
            var discovery = TextSurfaceDiscoveryResult.Success(surface);

            // Configure: Enter as Send, Ctrl+Enter as newline
            var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
                "codex-desktop",
                "Enter",
                "Ctrl+Enter",
                discovery);

            // Persist to store
            var saveResult = SubmitBindingProfileStore.Upsert(layout, profile);
            Assert.That(saveResult.Succeeded, Is.True);

            // Load profile from store
            var loadResult = SubmitBindingProfileStore.Load(layout);
            Assert.That(loadResult.Succeeded, Is.True);
            Assert.That(loadResult.Profiles, Has.Count.EqualTo(1));
            var persistedProfile = loadResult.Profiles[0];

            // Verify persisted binding values
            Assert.That(persistedProfile.SubmitBinding?.DisplayText, Is.EqualTo("Enter"));
            Assert.That(persistedProfile.NewlineBinding?.DisplayText, Is.EqualTo("Ctrl+Enter"));

            // Verify that NativeSubmitInterceptionController uses persisted values correctly
            var controller = new NativeSubmitInterceptionController(
                persistedProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));

            // Test: Enter (Send) should be guarded
            var sendGesture = controller.HandleGesture(new NativeKeyGesture("Enter"));
            Assert.That(sendGesture.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
            Assert.That(sendGesture.SuppressOriginalInput, Is.True);

            // Test: Ctrl+Enter (newline) should pass through
            var newlineGesture = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
            Assert.That(newlineGesture.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
            Assert.That(newlineGesture.SuppressOriginalInput, Is.False);
            Assert.That(newlineGesture.Diagnostics["pass_through_reason"], Is.EqualTo("newline_binding"));
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
            var stored = SubmitBindingProfileStore.Load(layout).Profiles.Single(profile => profile.ProfileId == "codex-desktop");

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
            Assert.That(result.State.UnprotectedProfileIds, Does.Contain("focused_supported_app"));
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
            Assert.That(result.State.UnprotectedProfileIds, Does.Contain("focused_supported_app"));
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
            var stored = SubmitBindingProfileStore.Load(layout).Profiles;
            var codexProfile = stored.Single(profile => profile.ProfileId == "codex-desktop");

            Assert.That(result.Succeeded, Is.True);
            Assert.That(stored, Has.Count.EqualTo(1));
            Assert.That(codexProfile.IsProtected, Is.True);
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

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.ProfilesUnavailable));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void WindowsTrayApp_UsesUnconfiguredProfileBeforeFirstVerification()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);

            var profile = WindowsTrayApp.ResolveNativeProfileForProtection(layout);

            Assert.That(profile, Is.Not.Null);
            Assert.That(profile!.ProfileId, Is.EqualTo("codex-desktop"));
            Assert.That(profile.IsProtected, Is.False);
            Assert.That(profile.SubmitBinding, Is.Null);
            Assert.That(profile.NewlineBinding, Is.Null);
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
        Assert.That(source, Does.Contain("AcceptButton = _verifyFocusedAppButton"));
    }

    [Test]
    public void FirstRunSetupController_VerifyFocusedProfileAutoDetectsTheActiveSupportedApp()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(CreateVerifiedChatGptDiscovery()),
                (_, _, _) => throw new InvalidOperationException("Setup window should not be shown by focused verification."));

            var result = controller.VerifyFocusedProfile("Ctrl+Enter", "Enter", layout);
            var stored = SubmitBindingProfileStore.Load(layout).Profiles;

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Diagnostics["profile_id"], Is.EqualTo("chatgpt-desktop"));
            Assert.That(result.PendingProfiles, Is.Not.Null);
            Assert.That(result.PendingProfiles!, Has.Count.EqualTo(1));
            Assert.That(result.PendingProfiles!.Single().ProfileId, Is.EqualTo("chatgpt-desktop"));
            Assert.That(result.PendingProfiles!.Single().IsProtected, Is.True);
            Assert.That(stored, Is.Empty);
            Assert.That(controller.GetSetupStatus(layout).State.Required, Is.True);
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
    public void FirstRunSetupController_ClosesSetupWithAnUncommittedFocusedCandidate()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(CreateVerifiedChatGptDiscovery()),
                (_, setupLayout, setupController) =>
                {
                    var focusedController = (IFocusedProfileSetupController)setupController;
                    var verification = focusedController.VerifyFocusedProfile("Ctrl+Enter", "Enter", setupLayout);
                    Assert.That(verification.Succeeded, Is.True);
                    return true;
                });

            var result = controller.ConfigureFocusedProfile(layout);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.PendingProfiles, Is.Not.Null);
            Assert.That(SubmitBindingProfileStore.Load(layout).Profiles, Is.Empty);
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
    public void FirstRunSetupController_VerifyFocusedProfilePublishesResidentLifecycleProgress()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var progress = new List<PromptProtectionSetupProgress>();
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(CreateVerifiedChatGptDiscovery()),
                (_, _, _) => throw new InvalidOperationException("Setup window should not be shown by focused verification."),
                setupProgressPublisher: progress.Add);

            var result = controller.VerifyFocusedProfile("Ctrl+Enter", "Enter", layout);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(progress.Select(item => item.Status), Is.EqualTo(new[]
            {
                "waiting_for_focus",
                "verifying_binding",
                "activating_protection"
            }));
            Assert.That(progress.All(item => item.Binding == "Ctrl+Enter"), Is.True);
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
    public void FirstRunSetupController_OpensUnifiedSetupWhenNoProfileIsVerified()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            IReadOnlyList<SubmitBindingProfile>? shownProfiles = null;
            var controller = new FirstRunSetupController(
                new FixedVerificationProfileVerifier(failure: true),
                (profiles, _, _) =>
                {
                    shownProfiles = profiles;
                    return false;
                });

            var result = controller.EnsureSetup(layout);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("setup_cancelled"));
            Assert.That(shownProfiles, Is.Not.Null);
            Assert.That(shownProfiles, Is.Empty);
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
    public void FirstRunSetupForm_UsesOneActiveAppVerificationAction()
    {
        var source = ProductSourceText("FirstRunSetup.cs");

        Assert.That(source, Does.Contain("Verify active app"));
        Assert.That(source, Does.Contain("VerifyFocusedProfile"));
        Assert.That(source, Does.Not.Contain("Protect this app:"));
        Assert.That(source, Does.Contain("Waiting for focus:"));
        Assert.That(source, Does.Not.Contain("Select Codex Desktop or ChatGPT Desktop before verification."));
    }

    [Test]
    public void FirstRunSetupController_GetSetupStatusRequiresSetupWhenOnlyAnUnselectedProtectedProfileExists()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Upsert(layout, CreateProtectedProfile() with { ProfileId = "chatgpt-desktop" });
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(CreateVerifiedChatGptDiscovery()),
                (_, _, _) => throw new InvalidOperationException("GetSetupStatus must be side-effect free."));

            var result = controller.GetSetupStatus(layout);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("setup_required"));
            Assert.That(result.State.Required, Is.True);
            Assert.That(result.Diagnostics["active_target_status"], Is.EqualTo("target_missing"));
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
    public void FirstRunSetupController_GetSetupStatusCompletesOnlyForThePersistedActiveTarget()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Upsert(layout, CreateProtectedProfile() with { ProfileId = "chatgpt-desktop" });
            Assert.That(ActivePromptProtectionTargetStore.Save(layout, "chatgpt-desktop").Succeeded, Is.True);
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(CreateVerifiedChatGptDiscovery()),
                (_, _, _) => throw new InvalidOperationException("GetSetupStatus must be side-effect free."));

            var result = controller.GetSetupStatus(layout);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.State.Required, Is.False);
            Assert.That(result.Diagnostics["active_target_profile_id"], Is.EqualTo("chatgpt-desktop"));
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
    public void FirstRunSetupLaunchCoordinator_StartsAutomaticSetupWhenOnlyAnOldProtectedProfileExists()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            SubmitBindingProfileStore.Upsert(layout, CreateProtectedProfile() with { ProfileId = "chatgpt-desktop" });
            var setupWindowCalls = 0;
            var controller = new FirstRunSetupController(
                new StaticFirstRunProfileVerifier(CreateVerifiedChatGptDiscovery()),
                (_, _, _) =>
                {
                    setupWindowCalls++;
                    return false;
                });

            var result = new FirstRunSetupLaunchCoordinator(layout, controller).RunIfRequired();

            Assert.That(setupWindowCalls, Is.EqualTo(1));
            Assert.That(result.Code, Is.EqualTo("setup_cancelled"));
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
    public void FirstRunSetupController_VerifyFocusedProfileContainsVerifierFailure()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var controller = new FirstRunSetupController(
                new ThrowingFocusedFirstRunProfileVerifier(),
                (_, _, _) => throw new InvalidOperationException("Setup window should not be shown by focused verification."));

            var result = controller.VerifyFocusedProfile("Enter", "Ctrl+Enter", layout);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("focused_profile_verification_failed"));
            Assert.That(result.Diagnostics["verification_exception"], Is.EqualTo("true"));
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

    private sealed class CapturingSubmitAction : ISubmitAction
    {
        public TextSurfaceDescriptor? LastSurface { get; private set; }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            LastSurface = surface;
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string>
            {
                ["submit_binding"] = surface.Metadata.TryGetValue("submit_binding") ?? "unknown"
            });
        }
    }

    internal sealed class FakeNativeSubmitHookHost : INativeSubmitHookHost, INativeSubmitPointerHookHost
    {
        private Func<NativeKeyGesture, NativeSubmitInterceptionResult>? _classify;
        private Action<NativeKeyGesture, NativeSubmitInterceptionResult>? _onSuppressedSubmit;
        private Func<NativeKeyGesture, bool>? _shouldSuppressClassificationFailure;
        private Func<NativePointerGesture, NativeSubmitInterceptionResult>? _classifyPointer;
        private Action<NativePointerGesture, NativeSubmitInterceptionResult>? _onSuppressedPointerSubmit;
        private Func<NativePointerGesture, bool>? _shouldSuppressPointerClassificationFailure;

        public bool Started { get; private set; }

        public bool StartResult { get; set; } = true;

        public Action<FakeNativeSubmitHookHost>? OnStarted { get; set; }

        public string? LastErrorCode { get; private set; }

        public NativeSubmitInterceptionResult? LastClassification { get; private set; }

        public NativeSubmitInterceptionResult? LastPointerClassification { get; private set; }

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture, NativeSubmitInterceptionResult> onSuppressedSubmit,
            Func<NativeKeyGesture, bool> shouldSuppressClassificationFailure)
        {
            _classify = classify;
            _onSuppressedSubmit = onSuppressedSubmit;
            _shouldSuppressClassificationFailure = shouldSuppressClassificationFailure;
            Started = StartResult;
            if (Started)
            {
                OnStarted?.Invoke(this);
            }

            return Started;
        }

        public void Stop()
        {
            Started = false;
            _classify = null;
            _onSuppressedSubmit = null;
            _shouldSuppressClassificationFailure = null;
            _classifyPointer = null;
            _onSuppressedPointerSubmit = null;
            _shouldSuppressPointerClassificationFailure = null;
        }

        public bool StartPointer(
            Func<NativePointerGesture, NativeSubmitInterceptionResult> classify,
            Action<NativePointerGesture, NativeSubmitInterceptionResult> onSuppressedSubmit,
            Func<NativePointerGesture, bool> shouldSuppressClassificationFailure)
        {
            _classifyPointer = classify;
            _onSuppressedPointerSubmit = onSuppressedSubmit;
            _shouldSuppressPointerClassificationFailure = shouldSuppressClassificationFailure;
            return StartResult;
        }

        public void Trigger(NativeKeyGesture gesture)
        {
            var result = _classify!(gesture);
            LastClassification = result;
            if (result.SuppressOriginalInput)
            {
                _onSuppressedSubmit!(gesture, result);
            }
        }

        public void TriggerPointer(NativePointerGesture gesture)
        {
            var result = _classifyPointer!(gesture);
            LastPointerClassification = result;
            if (result.SuppressOriginalInput)
            {
                _onSuppressedPointerSubmit!(gesture, result);
            }
        }

        public NativeSubmitInterceptionResult TriggerKeyboardClassificationFailure(NativeKeyGesture gesture)
        {
            var suppress = _shouldSuppressClassificationFailure!(gesture);
            var result = new NativeSubmitInterceptionResult(
                suppress ? OsInteractionStatusIds.SurfaceUnverified : OsInteractionStatusIds.NativeSubmitPassThrough,
                SuppressOriginalInput: suppress,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>());
            LastClassification = result;
            return result;
        }

        public NativeSubmitInterceptionResult TriggerPointerClassificationFailure(NativePointerGesture gesture)
        {
            var suppress = _shouldSuppressPointerClassificationFailure!(gesture);
            var result = new NativeSubmitInterceptionResult(
                suppress ? OsInteractionStatusIds.SurfaceUnverified : OsInteractionStatusIds.NativeSubmitPassThrough,
                SuppressOriginalInput: suppress,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>());
            LastPointerClassification = result;
            return result;
        }
    }

    internal sealed class FakeTrayHotkeyHost : ITrayHotkeyHost
    {
        private Action? _onTriggered;

        public HotkeyBinding Binding { get; } = new("fake-hotkey", "Ctrl+Enter", "test");

        public string? LastErrorCode { get; private set; }

        public bool Start(Action onTriggered)
        {
            _onTriggered = onTriggered;
            return true;
        }

        public void Stop()
        {
            _onTriggered = null;
        }

        public void Trigger()
        {
            _onTriggered?.Invoke();
        }
    }

    protected sealed class FixedFirstRunSetupController : IFirstRunSetupController
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

        public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null)
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

    private sealed class StaticFirstRunProfileVerifier : IFirstRunProfileVerifier, IFocusedFirstRunProfileVerifier
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

        public FocusedProfileVerificationResult VerifyFocused(string submitBinding, string newlineBinding)
        {
            var profileId = _discovery.Surface?.ProfileId;
            var profile = profileId is null
                ? null
                : FirstRunSetupController.CreateDefaultSetupProfile(profileId);
            if (profile is null)
            {
                return new FocusedProfileVerificationResult(
                    Profile: null,
                    Code: "focused_surface_unverified",
                    Diagnostics: new Dictionary<string, string>
                    {
                        ["surface_status"] = _discovery.Status
                    });
            }

            var verified = SubmitBindingOnboardingVerifier.VerifyUserBindings(
                profile.ProfileId,
                submitBinding,
                newlineBinding,
                _discovery,
                profile.CompatibilityEvidence);
            return new FocusedProfileVerificationResult(
                verified,
                verified.IsProtected ? "focused_profile_verified" : "focused_profile_verification_failed",
                verified.Diagnostics);
        }
    }

    private sealed class ThrowingFocusedFirstRunProfileVerifier : IFirstRunProfileVerifier, IFocusedFirstRunProfileVerifier
    {
        public SubmitBindingProfile Verify(SubmitBindingProfile profile)
        {
            return profile;
        }

        public FocusedProfileVerificationResult VerifyFocused(string submitBinding, string newlineBinding)
        {
            throw new InvalidOperationException("Injected verification failure.");
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
            Assert.That(result.State.UnprotectedProfileIds, Is.EqualTo(new[] { "focused_supported_app" }));
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
        var chatgptProfile = CreateVerifiedChatGptProfile();

        var codexController = new NativeSubmitInterceptionController(
            codexProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: setup);

        var chatgptController = new NativeSubmitInterceptionController(
            chatgptProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: CreateVerifiedChatGptDiscovery,
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
    public void TrayProtectionController_SameRuntimeGenerationChangeAtTracePublicationBoundaryCarriesTerminalTrace()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        TrayProtectionController? tray = null;
        var publicationInvalidations = 0;
        var generationChanged = false;
        var submitCalls = 0;
        tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)))),
            profile,
            () => submitCalls++,
            beforeProtectedSendTracePublishForTesting: () =>
            {
                if (publicationInvalidations++ == 0)
                {
                    generationChanged = tray!.TryPublishLocalProtectionReady();
                }
            });

        Assert.That(tray.Start(), Is.True);
        var sourceGeneration = tray.GetCurrentSnapshot().Generation;
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(publicationInvalidations, Is.GreaterThanOrEqualTo(1));
        Assert.That(generationChanged, Is.True);
        Assert.That(submitCalls, Is.Zero);
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "terminal_blocked"
        }));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.All(entry => entry.SnapshotGeneration == sourceGeneration), Is.True);
    }

    [Test]
    public void TrayProtectionController_StateChangedFailureDoesNotStrandProtectedSendOperation()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)))),
            profile,
            () => submitCalls++);
        EventHandler throwingObserver = (_, _) => throw new InvalidOperationException("test observer failure");

        Assert.That(tray.Start(), Is.True);
        tray.StateChanged += throwingObserver;
        Assert.Throws<InvalidOperationException>(() => hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true)));
        tray.StateChanged -= throwingObserver;

        Assert.That(tray.State.NativeSubmitEnabled, Is.False);
        Assert.That(tray.TryPublishLocalProtectionReady(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(submitCalls, Is.EqualTo(1));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Last().Stage, Is.EqualTo("sent_safely"));
    }

    [Test]
    public void TrayProtectionController_GenerationChangeCarriesActiveTraceToTerminalOutcome()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)))),
            profile,
            () => submitCalls++);
        var generationChanged = false;
        tray.StateChanged += (_, _) =>
        {
            if (!generationChanged && tray.State.ProtectedSendAttemptStatus == "detected")
            {
                generationChanged = tray.TryPublishLocalProtectionReady();
            }
        };

        Assert.That(tray.Start(), Is.True);
        var sourceGeneration = tray.GetCurrentSnapshot().Generation;
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(generationChanged, Is.True);
        Assert.That(submitCalls, Is.Zero);
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "terminal_blocked"
        }));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.All(entry => entry.SnapshotGeneration == sourceGeneration), Is.True);
        Assert.That(tray.State.LastProtectedSendInterruption!.Reason, Is.EqualTo("runtime_replaced"));
        Assert.That(tray.GetCurrentSnapshot().Generation, Is.GreaterThan(sourceGeneration));
    }

    [Test]
    public void TrayProtectionController_StopAtTracePublicationBoundaryPublishesOnlyTerminalOutcome()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        TrayProtectionController? tray = null;
        using var stopRequested = new ManualResetEventSlim(false);
        Thread? stopThread = null;
        var boundaryEntered = false;
        var cancellationObserved = false;
        var submitCalls = 0;
        var publishedStages = new List<string>();
        tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)))),
            profile,
            () => submitCalls++,
            protectedSendStageObserver: stage =>
            {
                if (stage == "stop_cancellation_requested")
                {
                    stopRequested.Set();
                }
            },
            beforeProtectedSendTracePublishForTesting: () =>
            {
                if (!boundaryEntered)
                {
                    boundaryEntered = true;
                    stopThread = new Thread(() => tray!.Stop());
                    stopThread.Start();
                    cancellationObserved = stopRequested.Wait(TimeSpan.FromSeconds(1));
                }
            });

        Assert.That(tray.Start(), Is.True);
        tray.SetProtectedSendTracePublishedObserverForTesting(entry => publishedStages.Add(entry.Stage));
        var sourceGeneration = tray.GetCurrentSnapshot().Generation;
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        stopThread!.Join();

        Assert.That(cancellationObserved, Is.True);
        Assert.That(submitCalls, Is.Zero);
        Assert.That(tray.State.Enabled, Is.False);
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "terminal_blocked"
        }));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.All(entry => entry.SnapshotGeneration == sourceGeneration), Is.True);
        Assert.That(publishedStages, Is.Empty.Or.EquivalentTo(new[] { "terminal_blocked" }));
        Assert.That(tray.State.LastProtectedSendInterruption!.Reason, Is.EqualTo("protection_stopped"));
    }

    [Test]
    public void TrayProtectionController_ReloadAtTracePublicationBoundaryCarriesOnlyOldGenerationTrace()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var replacementHook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        TrayProtectionController? tray = null;
        using var reloadRequested = new ManualResetEventSlim(false);
        Thread? reloadThread = null;
        var boundaryEntered = false;
        var cancellationObserved = false;
        var reloaded = false;
        var oldSubmitCalls = 0;
        var replacementSubmitCalls = 0;
        var publishedStages = new List<string>();
        var replacementRuntime = NativeSubmitRuntime.CreateTest(
            replacementHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                replacementSubmitCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            profile);
        tray = CreatePointerTray(
            oldHook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(profile.ProfileId)))),
            profile,
            () => oldSubmitCalls++,
            protectedSendStageObserver: stage =>
            {
                if (stage == "reload_cancellation_requested")
                {
                    reloadRequested.Set();
                }
            },
            beforeProtectedSendTracePublishForTesting: () =>
            {
                if (!boundaryEntered)
                {
                    boundaryEntered = true;
                    reloadThread = new Thread(() => reloaded = tray!.ReloadNativeSubmit(replacementRuntime));
                    reloadThread.Start();
                    cancellationObserved = reloadRequested.Wait(TimeSpan.FromSeconds(1));
                }
            });

        Assert.That(tray.Start(), Is.True);
        tray.SetProtectedSendTracePublishedObserverForTesting(entry => publishedStages.Add(entry.Stage));
        var sourceGeneration = tray.GetCurrentSnapshot().Generation;
        oldHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        reloadThread!.Join();

        Assert.That(cancellationObserved, Is.True);
        Assert.That(reloaded, Is.True);
        Assert.That(oldSubmitCalls, Is.Zero);
        Assert.That(replacementSubmitCalls, Is.Zero);
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "terminal_blocked"
        }));
        Assert.That(tray.State.LastProtectedSendInterruption!.Reason, Is.EqualTo("runtime_replaced"));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.All(entry => entry.SnapshotGeneration == sourceGeneration), Is.True);
        Assert.That(publishedStages, Is.Empty.Or.EquivalentTo(new[] { "terminal_blocked" }));
        Assert.That(tray.GetCurrentSnapshot().Generation, Is.GreaterThan(sourceGeneration));
    }

    [Test]
    public void TrayProtectionController_RoutesKeyboardAndMouseSendThroughTheSameProtectedFlow()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSurfaceWithWindow("codex-desktop", "2A")))),
            profile,
            () => submitCalls++);

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        hook.TriggerPointer(new NativePointerGesture(10, 10, "left", new IntPtr(0x2A), 7));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(submitCalls, Is.EqualTo(2));
    }

    [Test]
    public void TrayProtectionController_PointerSendPublishesItsOwnResidentTrace()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSurfaceWithWindow("codex-desktop", "2A")))),
            profile,
            () => submitCalls++);

        Assert.That(tray.Start(), Is.True);
        hook.TriggerPointer(new NativePointerGesture(10, 10, "left", new IntPtr(0x2A), 7));

        Assert.That(submitCalls, Is.EqualTo(1));
        Assert.That(tray.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(tray.State.ProtectedSendAttemptStatus, Is.EqualTo("sent_safely"));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "target_matched",
            "composer_read",
            "sanitized",
            "send_injected",
            "sent_safely"
        }));
    }

    [Test]
    public void TrayProtectionController_PointerSendWithoutTargetIdentityRemainsFailClosed()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")))),
            profile,
            () => submitCalls++);

        Assert.That(tray.Start(), Is.True);
        hook.TriggerPointer(new NativePointerGesture(10, 10, "left", new IntPtr(0x2A), 7));

        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(hook.LastPointerClassification?.SuppressOriginalInput, Is.True);
        Assert.That(submitCalls, Is.Zero);
        Assert.That(tray.State.ProtectedSendAttemptStatus, Is.EqualTo("trace_unavailable"));
    }

    [Test]
    public void TrayProtectionController_PointerTraceFailureRemainsFailClosed()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(
                    CreateNativeSubmitSurface(profile.ProfileId))),
            TrayProtectionController.TraceRunnerUnavailableResult,
            profile);
        var tray = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            sendControlDiscovery: new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSurfaceWithWindow(profile.ProfileId, "2A")))),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(
                CreateNativeSubmitSurface(profile.ProfileId)),
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(tray.Start(), Is.True);
        hook.TriggerPointer(new NativePointerGesture(10, 10, "left", new IntPtr(0x2A), 7));

        Assert.That(tray.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.TraceUnavailable));
        Assert.That(tray.State.LastSubmitted, Is.False);
        Assert.That(tray.State.ProtectedSendAttemptStatus, Is.EqualTo("trace_unavailable"));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Last().Stage, Is.EqualTo("terminal_blocked"));
    }

    [Test]
    public void TrayProtectionController_ProfileStoreFailureShowsSettingsRecoveryAndBlocksSend()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")))),
            profile,
            () => submitCalls++,
            firstRunSetupController: FixedFirstRunSetupController.FailedFor("codex-desktop"));

        Assert.That(tray.Start(), Is.True);
        Assert.That(tray.State.SetupRequired, Is.False);
        Assert.That(tray.State.NativeSubmitStatus, Is.EqualTo(OsInteractionStatusIds.ProfilesUnavailable));
        Assert.That(tray.State.NativeSubmitEnabled, Is.False);

        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.ProfilesUnavailable));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.True);
        Assert.That(submitCalls, Is.Zero);

        var statusText = WindowsTrayApplicationContext.FormatReadableProtectionStatus(tray.State);
        Assert.That(statusText, Does.Contain("profile settings"));
        var statusView = LocalProtectionStatusView.Create(tray.State);
        Assert.That(statusView.Rows[1].Action, Is.EqualTo(LocalProtectionStatusAction.RepairProfileSettings));
    }

    [Test]
    public void TrayProtectionController_StopPublishesDisabledGenerationBeforeQueuedCallbackCanStart()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runnerCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")))),
            profile,
            () => runnerCalls++);
        var callbackObservedDisabled = false;
        tray.StateChanged += (_, _) =>
        {
            if (!callbackObservedDisabled && !tray.State.Enabled)
            {
                callbackObservedDisabled = true;
                hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
            }
        };

        Assert.That(tray.Start(), Is.True);
        tray.Stop();

        Assert.That(callbackObservedDisabled, Is.True);
        Assert.That(runnerCalls, Is.Zero);
        Assert.That(tray.State.Enabled, Is.False);
        Assert.That(tray.State.NativeSubmitEnabled, Is.False);
    }

    [Test]
    public void TrayProtectionController_StopBeforeFirstTracePublishesTerminalTrace()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runnerCalls = 0;
        TrayProtectionController? tray = null;
        tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.IdentifiedSend,
                TextSurfaceDiscoveryResult.Success(CreateNativeSurfaceWithWindow("codex-desktop", "2A")))),
            profile,
            () => runnerCalls++,
            protectedSendStageObserver: stage =>
            {
                if (stage == "operation_started")
                {
                    tray!.Stop();
                }
            });

        Assert.That(tray.Start(), Is.True);
        hook.TriggerPointer(new NativePointerGesture(10, 10, "left", new IntPtr(0x2A), 7));

        Assert.That(runnerCalls, Is.Zero);
        Assert.That(tray.State.Enabled, Is.False);
        Assert.That(tray.State.ProtectedSendAttemptStatus, Is.EqualTo("trace_unavailable"));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "terminal_blocked"
        }));
        Assert.That(tray.State.ProtectedSendAttemptTrace!.Last().ResultCode, Is.EqualTo(OsInteractionStatusIds.FailedClosed));
        Assert.That(tray.State.LastProtectedSendInterruption!.Reason, Is.EqualTo("protection_stopped"));
    }

    [TestCase("Enter")]
    [TestCase("Space")]
    public void TrayProtectionController_RoutesFocusedKeyboardSendThroughTheProtectedFlow(string key)
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var focusedSend = new SendControlDiscoveryResult(
            SendControlClassification.IdentifiedSend,
            TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(focusedSend, focusedSend),
            profile,
            () => submitCalls++,
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture(key));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(submitCalls, Is.EqualTo(1));
    }

    [Test]
    public void TrayProtectionController_PassesThroughFocusedKeyboardNonSendControl()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var nonSend = new SendControlDiscoveryResult(
            SendControlClassification.NonSendControl,
            TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(nonSend, nonSend),
            profile,
            () => throw new AssertionException("Non-Send input must not submit."),
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true, TargetWindow: new IntPtr(1)));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_DoesNotTreatEveryKeyOnFocusedSendControlAsSubmit()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var focusedSend = new SendControlDiscoveryResult(
            SendControlClassification.IdentifiedSend,
            TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(focusedSend, focusedSend),
            profile,
            () => throw new AssertionException("Only the verified submit binding may submit."),
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("A"));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_SuppressesFocusedControlUncertaintyForTheSelectedSubmitBinding()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var uncertain = new SendControlDiscoveryResult(
            SendControlClassification.SelectedClientUncertain,
            TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.SurfaceUnverified,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(uncertain, uncertain),
            profile,
            () => throw new AssertionException("An uncertain control must not submit."),
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void TrayProtectionController_PassesThroughFocusedControlUncertaintyForNonSubmitInput()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var uncertain = new SendControlDiscoveryResult(
            SendControlClassification.SelectedClientUncertain,
            TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.SurfaceUnverified,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(uncertain, uncertain),
            profile,
            () => throw new AssertionException("Non-submit input must not submit."),
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("A"));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_DoesNotRouteAnUnselectedFocusedSendControlToTheOnlyRuntime()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var unselectedSend = new SendControlDiscoveryResult(
            SendControlClassification.IdentifiedSend,
            TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("chatgpt-desktop")));
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(unselectedSend, unselectedSend),
            profile,
            () => throw new AssertionException("An unselected profile must not submit through Codex."),
            activeProfileId: "chatgpt-desktop",
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "chatgpt-desktop" }));

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter"));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_PassesThroughSelectedNonSendControl()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.NonSendControl,
                TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.NotComposer,
                    new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }))),
            profile,
            () => submitCalls++);

        Assert.That(tray.Start(), Is.True);
        hook.TriggerPointer(new NativePointerGesture(20, 20, "left"));

        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(hook.LastPointerClassification?.SuppressOriginalInput, Is.False);
        Assert.That(submitCalls, Is.EqualTo(0));
    }

    [Test]
    public void TrayProtectionController_SuppressesUncertainSelectedSendControl()
    {
        const string sensitiveControlValue = "SEND_CONTROL_C195C3D8E8F3";
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.SelectedClientUncertain,
                TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.SurfaceUnverified,
                    new Dictionary<string, string>
                    {
                        ["profile_id"] = "codex-desktop",
                        ["control_name"] = sensitiveControlValue
                    }))),
            profile,
            () => submitCalls++);

        Assert.That(tray.Start(), Is.True);
        hook.TriggerPointer(new NativePointerGesture(30, 30, "left"));

        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(hook.LastPointerClassification?.SuppressOriginalInput, Is.True);
        Assert.That(System.Text.Json.JsonSerializer.Serialize(hook.LastPointerClassification), Does.Not.Contain(sensitiveControlValue));
        Assert.That(submitCalls, Is.EqualTo(0));
    }

    [Test]
    public void TrayProtectionController_PassesThroughUnrelatedPointerUncertainty()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var submitCalls = 0;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()))),
            profile,
            () => submitCalls++);

        Assert.That(tray.Start(), Is.True);
        hook.TriggerPointer(new NativePointerGesture(40, 40, "left"));

        Assert.That(hook.LastPointerClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(hook.LastPointerClassification?.SuppressOriginalInput, Is.False);
        Assert.That(submitCalls, Is.EqualTo(0));
    }

    [Test]
    public void TrayProtectionController_SuppressesSelectedSubmitWhenKeyboardClassificationFails()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()))),
            profile,
            () => { });

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("A", TargetWindow: new IntPtr(42), TargetProcessId: 7));
        var result = hook.TriggerKeyboardClassificationFailure(new NativeKeyGesture(
            "Enter",
            Ctrl: true,
            TargetWindow: new IntPtr(42),
            TargetProcessId: 7));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void TrayProtectionController_SuppressesSelectedSubmitWhenWorkerResolverFails()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var resolverFails = false;
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()))),
            profile,
            () => { },
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string>()),
            selectedWindowProfileResolver: _ => resolverFails
                ? throw new InvalidOperationException("worker resolver failed")
                : "codex-desktop");

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("A", TargetWindow: new IntPtr(42), TargetProcessId: 7));
        resolverFails = true;
        var result = hook.TriggerKeyboardClassificationFailure(new NativeKeyGesture(
            "Enter",
            Ctrl: true,
            TargetWindow: new IntPtr(42),
            TargetProcessId: 7));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void TrayProtectionController_PassesThroughUnrelatedKeyboardClassificationFailure()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()))),
            profile,
            () => { },
            activeProfileId: "unselected-client");

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("A", TargetWindow: new IntPtr(42), TargetProcessId: 7));
        var result = hook.TriggerKeyboardClassificationFailure(new NativeKeyGesture(
            "Enter",
            Ctrl: true,
            TargetWindow: new IntPtr(42),
            TargetProcessId: 7));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_DoesNotReuseSelectedWindowCacheForAnotherProcess()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()))),
            profile,
            () => { },
            selectedWindowProfileResolver: _ => null);

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("A", TargetWindow: new IntPtr(42), TargetProcessId: 7));
        var result = hook.TriggerKeyboardClassificationFailure(new NativeKeyGesture(
            "Enter",
            Ctrl: true,
            TargetWindow: new IntPtr(42),
            TargetProcessId: 8));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void WindowsNativeSubmitHookHost_CachesOnlySelectedCapturedTargets()
    {
        var host = new WindowsNativeSubmitHookHost();
        var selected = new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.SurfaceUnverified,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string> { ["profile_id"] = "codex-desktop" });
        var unrelated = new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.NativeSubmitPassThrough,
            SuppressOriginalInput: false,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>());

        host.RememberSelectedTargetForTest(new IntPtr(42), 7, selected);
        host.RememberSelectedTargetForTest(new IntPtr(43), 7, unrelated);

        Assert.That(host.IsSelectedTargetForTest(new IntPtr(42), 7), Is.True);
        Assert.That(host.IsSelectedTargetForTest(new IntPtr(42), 8), Is.False);
        Assert.That(host.IsSelectedTargetForTest(new IntPtr(43), 7), Is.False);
    }

    [Test]
    public void WindowsNativeSubmitHookHost_FaultedFallbackBlocksOnlyCachedSelectedTarget()
    {
        var host = new WindowsNativeSubmitHookHost();
        var selected = new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.SurfaceUnverified, true, false, false,
            new Dictionary<string, string> { ["profile_id"] = "codex-desktop" });
        host.RememberSelectedTargetForTest(new IntPtr(42), 7, selected);

        var keyboardSelected = host.InvokeKeyboardFallbackForTest(
            new NativeKeyGesture("Enter", TargetWindow: new IntPtr(42), TargetProcessId: 7),
            _ => throw new InvalidOperationException("synthetic"));
        var keyboardUnrelated = host.InvokeKeyboardFallbackForTest(
            new NativeKeyGesture("Enter", TargetWindow: new IntPtr(43), TargetProcessId: 7),
            _ => throw new InvalidOperationException("synthetic"));
        var pointerSelected = host.InvokePointerFallbackForTest(
            new NativePointerGesture(0, 0, "left", new IntPtr(42), 7),
            _ => throw new InvalidOperationException("synthetic"));
        var pointerUnrelated = host.InvokePointerFallbackForTest(
            new NativePointerGesture(0, 0, "left", new IntPtr(43), 7),
            _ => throw new InvalidOperationException("synthetic"));

        Assert.That(keyboardSelected, Is.True);
        Assert.That(pointerSelected, Is.True);
        Assert.That(keyboardUnrelated, Is.False);
        Assert.That(pointerUnrelated, Is.False);
    }

    [Test]
    public void WindowsNativeSubmitHookHost_ConfiguresPointerCallbacksWithoutRegisteringGlobalMouseHook()
    {
        var host = new WindowsNativeSubmitHookHost();

        var started = host.StartPointer(
            _ => new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.NativeSubmitPassThrough,
                SuppressOriginalInput: false,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()),
            (_, _) => { },
            _ => false);

        Assert.That(started, Is.True);
        Assert.That(host.IsMouseHookRegistered, Is.False);
    }

    [Test]
    public void TrayProtectionController_PointerClassificationFailureDoesNotResolveLiveTargetWithoutCache()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.NotComposer,
                    new Dictionary<string, string>()))),
            profile,
            () => { },
            selectedWindowProfileResolver: window => window == new IntPtr(42)
                ? "codex-desktop"
                : "other-app");

        Assert.That(tray.Start(), Is.True);

        var selected = hook.TriggerPointerClassificationFailure(
            new NativePointerGesture(0, 0, "left", new IntPtr(42), 7));
        var unrelated = hook.TriggerPointerClassificationFailure(
            new NativePointerGesture(0, 0, "left", new IntPtr(43), 7));

        Assert.That(selected.SuppressOriginalInput, Is.False);
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_PointerClassificationFailureBlocksOnlyCachedSelectedTarget()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.NotComposer,
                    new Dictionary<string, string>()))),
            profile,
            () => throw new AssertionException("A failed pointer classification must not submit."),
            selectedWindowProfileResolver: window => window == new IntPtr(42)
                ? "codex-desktop"
                : "other-app");

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("A", TargetWindow: new IntPtr(42), TargetProcessId: 7));

        var selected = hook.TriggerPointerClassificationFailure(
            new NativePointerGesture(0, 0, "left", new IntPtr(42), 7));
        var unrelated = hook.TriggerPointerClassificationFailure(
            new NativePointerGesture(0, 0, "left", new IntPtr(43), 7));

        Assert.That(selected.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(selected.SuppressOriginalInput, Is.True);
        Assert.That(selected.Submitted, Is.False);
        Assert.That(unrelated.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_KeyboardClassificationFailureDoesNotResolveLiveTargetWithoutCache()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.NotComposer,
                    new Dictionary<string, string>()))),
            profile,
            () => { },
            selectedWindowProfileResolver: window => window == new IntPtr(42)
                ? "codex-desktop"
                : "other-app");

        Assert.That(tray.Start(), Is.True);

        var selected = hook.TriggerKeyboardClassificationFailure(
            new NativeKeyGesture("Enter", Ctrl: true, TargetWindow: new IntPtr(42), TargetProcessId: 7));
        var unrelated = hook.TriggerKeyboardClassificationFailure(
            new NativeKeyGesture("Enter", Ctrl: true, TargetWindow: new IntPtr(43), TargetProcessId: 7));

        Assert.That(selected.SuppressOriginalInput, Is.False);
        Assert.That(unrelated.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_FocusedSendUsesCapturedWindowWhenFocusChanges()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var focused = new FixedSendControlDiscovery(
            new SendControlDiscoveryResult(
                SendControlClassification.Unrelated,
                TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>())),
            new SendControlDiscoveryResult(
                SendControlClassification.SelectedClientUncertain,
                TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.SurfaceUnverified,
                    new Dictionary<string, string> { ["profile_id"] = "codex-desktop" })));
        var tray = CreatePointerTray(
            hook,
            focused,
            profile,
            () => { },
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }));
        var capturedWindow = new IntPtr(77);

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true, TargetWindow: capturedWindow, TargetProcessId: 7));

        Assert.That(focused.LastFocusedTargetWindow, Is.EqualTo(capturedWindow));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.True);
        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
    }

    [TestCase("Enter")]
    [TestCase("Space")]
    public void NativeSubmitInterception_FocusedSendDuringOnboardingReturnsSetupRequired(string key)
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var tray = CreatePointerTray(
            hook,
            new FixedSendControlDiscovery(
                new SendControlDiscoveryResult(
                    SendControlClassification.Unrelated,
                    TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>())),
                new SendControlDiscoveryResult(
                    SendControlClassification.IdentifiedSend,
                    TextSurfaceDiscoveryResult.Failure(
                        OsInteractionStatusIds.NotComposer,
                        new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }))),
            profile,
            () => { },
            activeSurfaceResult: TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }),
            firstRunSetupController: new ThrowingSetupStatusController());

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture(key, TargetWindow: new IntPtr(77), TargetProcessId: 7));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void SendControlEvidence_MatchesStableAutomationIdAfterLocalizedLabelChanges()
    {
        var evidence = SendControlEvidence.Create("sendButton", "Send");

        var matched = SendControlEvidence.Matches(evidence, "sendButton", "Enviar");

        Assert.That(matched, Is.True);
        Assert.That(string.Join("|", evidence.Values), Does.Not.Contain("sendButton"));
        Assert.That(string.Join("|", evidence.Values), Does.Not.Contain("Send"));
    }

    [Test]
    public void SendControlEvidence_DoesNotTreatAnUnrelatedButtonAsSend()
    {
        var evidence = SendControlEvidence.Create("sendButton", "Send");

        var matched = SendControlEvidence.Matches(evidence, "skillPicker", "Choose skill");

        Assert.That(matched, Is.False);
    }

    [Test]
    public void TrayProtectionController_SuppressesSelectedClientClassifierUncertainty()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("Guarded flow must not run."),
            profile);
        var tray = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.SurfaceUnverified,
                new Dictionary<string, string> { ["profile_id"] = "codex-desktop" }),
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void TrayProtectionController_PassesThroughClassifierUncertaintyOutsideSelectedClient()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("Guarded flow must not run."),
            profile);
        var tray = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.SurfaceUnverified,
                new Dictionary<string, string> { ["profile_id"] = "unrelated-app" }),
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.False);
    }

    [Test]
    public void TrayProtectionController_ClassifierExceptionKeepsSelectedSendGuarded()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var runnerCalls = 0;
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"))),
            () =>
            {
                runnerCalls++;
                return CreateSubmittedResult("codex-desktop");
            },
            profile);
        var tray = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            activeSurfaceDiscovery: () => throw new InvalidOperationException(),
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(hook.LastClassification?.SuppressOriginalInput, Is.True);
        Assert.That(runnerCalls, Is.EqualTo(1));
    }

    [Test]
    public void TrayProtectionController_AbortsDeferredFlowWhenCapturedTargetChanges()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var capturedSurface = CreateNativeSurfaceWithWindow("codex-desktop", "1");
        var changedSurface = CreateNativeSurfaceWithWindow("codex-desktop", "2");
        var targetRunnerCalls = 0;
        NativeSubmitTargetIdentity? capturedTarget = null;
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("Untargeted runner must not be used."),
            profile,
            ResidentTargetTracedRunner: (target, _, _, _) =>
            {
                targetRunnerCalls++;
                capturedTarget = target;
                var rediscovery = new CapturedTargetSurfaceDiscovery(
                    new FixedSurfaceDiscovery(changedSurface),
                    target).DiscoverActiveSurface();
                return new OsInteractionResult(
                    rediscovery.Status,
                    rediscovery.Surface,
                    null,
                    null,
                    Applied: false,
                    Submitted: false,
                    rediscovery.Diagnostics);
            });
        var tray = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(capturedSurface),
            nativeSubmitRuntimes: new[] { runtime });

        Assert.That(tray.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true, TargetWindow: new IntPtr(1)));

        Assert.That(targetRunnerCalls, Is.EqualTo(1));
        Assert.That(capturedTarget?.SnapshotGeneration, Is.EqualTo(tray.GetCurrentSnapshot().Generation));
        Assert.That(tray.State.LastStatus, Is.EqualTo(OsInteractionStatusIds.StaleComposer));
        Assert.That(tray.State.LastSubmitted, Is.False);
    }

    [Test]
    public void NativeSubmitInterception_SetupStatusExceptionSuppressesSelectedSend()
    {
        var controller = new NativeSubmitInterceptionController(
            CreateProtectedProfile(),
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: new ThrowingSetupStatusController());

        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Diagnostics["setup_status_error"], Is.EqualTo("true"));
    }

    [Test]
    public void FirstRunSetupController_VerificationExceptionKeepsSelectedProfileUnprotected()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var pendingProfile = CreateProtectedProfile() with
            {
                BindingSource = "not_verified",
                CapabilityStatus = OsInteractionStatusIds.BindingUnknown
            };
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { pendingProfile }).Succeeded, Is.True);
            var setup = new FirstRunSetupController(
                new ThrowingFirstRunProfileVerifier(),
                (_, _, _) => throw new InvalidOperationException("Setup window should not be shown."));

            var result = setup.VerifyProfile("codex-desktop", layout);
            var stored = SubmitBindingProfileStore.Load(layout).Profiles.Single();

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("verification_failed"));
            Assert.That(result.Diagnostics["verification_exception"], Is.EqualTo("true"));
            Assert.That(stored.IsProtected, Is.False);
            Assert.That(System.Text.Json.JsonSerializer.Serialize(result), Does.Not.Contain("synthetic prompt"));
        }

        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void SubmitBindingProfileStore_UpsertDoesNotOverwriteAnUnreadableExistingStore()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            Directory.CreateDirectory(layout.SettingsDirectory);
            const string invalidStore = "{ invalid-json";
            File.WriteAllText(SubmitBindingProfileStore.DefaultPath(layout), invalidStore);

            var result = SubmitBindingProfileStore.Upsert(layout, CreateProtectedProfile());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Code, Is.EqualTo("profiles_unavailable"));
            Assert.That(File.ReadAllText(SubmitBindingProfileStore.DefaultPath(layout)), Is.EqualTo(invalidStore));
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
    public void HandleIdentifiedSendControl_FailsClosedWhenComposerCannotBeVerified()
    {
        var controller = new NativeSubmitInterceptionController(
            CreateProtectedProfile(),
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)));

        var result = controller.HandleIdentifiedSendControl(
            TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()));

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.SurfaceUnverified));
        Assert.That(result.SuppressOriginalInput, Is.True);
        Assert.That(result.Submitted, Is.False);
    }

    private sealed class ThrowingFirstRunProfileVerifier : IFirstRunProfileVerifier
    {
        public SubmitBindingProfile Verify(SubmitBindingProfile profile)
        {
            throw new InvalidOperationException("synthetic prompt must not be persisted");
        }
    }

    private sealed class ThrowingSetupStatusController : IFirstRunSetupController
    {
        public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout) => throw new InvalidOperationException();

        public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null) => throw new InvalidOperationException();

        public FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout) => throw new InvalidOperationException();

        public bool IsSetupComplete(DefaultStorageLayout layout) => false;
    }

    private sealed class FixedSurfaceDiscovery : IActiveTextSurfaceDiscovery
    {
        private readonly TextSurfaceDescriptor _surface;

        public FixedSurfaceDiscovery(TextSurfaceDescriptor surface)
        {
            _surface = surface;
        }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface() => TextSurfaceDiscoveryResult.Success(_surface);
    }

    private sealed class FixedSendControlDiscovery : ISendControlDiscovery
    {
        private readonly SendControlDiscoveryResult _pointerResult;
        private readonly SendControlDiscoveryResult _focusedResult;

        public FixedSendControlDiscovery(
            SendControlDiscoveryResult pointerResult,
            SendControlDiscoveryResult? focusedResult = null)
        {
            _pointerResult = pointerResult;
            _focusedResult = focusedResult ?? pointerResult;
        }

        public SendControlDiscoveryResult Discover(NativePointerGesture gesture) => _pointerResult;

        public IntPtr? LastFocusedTargetWindow { get; private set; }

        public SendControlDiscoveryResult DiscoverFocusedControl(IntPtr capturedTargetWindow)
        {
            LastFocusedTargetWindow = capturedTargetWindow;
            return _focusedResult;
        }
    }

    private static TrayProtectionController CreatePointerTray(
        FakeNativeSubmitHookHost hook,
        ISendControlDiscovery sendControlDiscovery,
        SubmitBindingProfile profile,
        Action onSubmit,
        string activeProfileId = "codex-desktop",
        TextSurfaceDiscoveryResult? activeSurfaceResult = null,
        Func<IntPtr, string?>? selectedWindowProfileResolver = null,
        IFirstRunSetupController? firstRunSetupController = null,
        Action<string>? protectedSendStageObserver = null,
        Action? beforeProtectedSendTracePublishForTesting = null)
    {
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                firstRunSetupController: firstRunSetupController),
            () =>
            {
                onSubmit();
                return CreateSubmittedResult("codex-desktop");
            },
            profile);
        return TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtime.Controller,
            profile,
            sendControlDiscovery: sendControlDiscovery,
            activeSurfaceDiscovery: () => activeSurfaceResult
                ?? TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface(activeProfileId)),
            selectedWindowProfileResolver: selectedWindowProfileResolver ?? (_ => activeProfileId),
            protectedSendStageObserver: protectedSendStageObserver,
            nativeSubmitRuntimes: new[] { runtime },
            beforeProtectedSendTracePublishForTesting: beforeProtectedSendTracePublishForTesting);
    }

    private static TextSurfaceDescriptor CreateNativeSurfaceWithWindow(string profileId, string windowHandle)
    {
        var surface = CreateNativeSubmitSurface(profileId);
        return surface with
        {
            Metadata = surface.Metadata with { WindowHandle = windowHandle }
        };
    }

    [Test]
    public void NativeSubmitInterception_BindingChangeBlocksNewSubmitBeforeRuntimeReload()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var oldProfile = CreateProtectedProfile() with
            {
                SubmitBinding = SubmitKeyBinding.Parse("Enter").Binding,
                NewlineBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding
            };
            var changedProfile = oldProfile with
            {
                BindingSource = "not_verified",
                CapabilityStatus = OsInteractionStatusIds.BindingUnknown,
                SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
                NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
            };
            SubmitBindingProfileStore.Save(layout, new[] { changedProfile });
            var controller = new NativeSubmitInterceptionController(
                oldProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
                firstRunSetupController: new FirstRunSetupController(),
                setupLayout: layout);

            // Ctrl+Enter was the old newline key but is the newly selected Send
            // key. It must remain blocked until the new pair is verified and
            // atomically loaded into the resident runtime.
            var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));

            Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
            Assert.That(result.SuppressOriginalInput, Is.True);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void TrayProtectionController_ReloadsOnlyTheVerifiedBindingAfterPendingBindingChange()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var oldProfile = CreateProtectedProfile() with
            {
                SubmitBinding = SubmitKeyBinding.Parse("Enter").Binding,
                NewlineBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding
            };
            var pendingProfile = oldProfile with
            {
                BindingSource = "not_verified",
                CapabilityStatus = OsInteractionStatusIds.BindingUnknown,
                SubmitBinding = SubmitKeyBinding.Parse("Ctrl+Enter").Binding,
                NewlineBinding = SubmitKeyBinding.Parse("Enter").Binding
            };
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { pendingProfile }).Succeeded, Is.True);

            var oldHook = new FakeNativeSubmitHookHost();
            var oldSubmitCalls = 0;
            var oldRuntime = NativeSubmitRuntime.CreateTest(
                oldHook,
                new NativeSubmitInterceptionController(
                    oldProfile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                    activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
                    firstRunSetupController: new FirstRunSetupController(),
                    setupLayout: layout),
                () =>
                {
                    oldSubmitCalls++;
                    return CreateSubmittedResult("codex-desktop");
                },
                oldProfile);
            var tray = TrayProtectionController.CreateTest(
                new FakeTrayHotkeyHost(),
                () => throw new InvalidOperationException("Manual scan should not run."),
                oldHook,
                oldRuntime.Controller,
                oldProfile,
                storageLayout: layout,
                activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
                nativeSubmitRuntimes: new[] { oldRuntime });

            Assert.That(tray.Start(), Is.True);
            oldHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
            Assert.That(oldHook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitSetupRequired));
            Assert.That(oldSubmitCalls, Is.EqualTo(0));

            var verifiedProfile = pendingProfile with
            {
                BindingSource = "user_verified",
                CapabilityStatus = OsInteractionStatusIds.Protected
            };
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { verifiedProfile }).Succeeded, Is.True);

            var verifiedHook = new FakeNativeSubmitHookHost();
            var verifiedSubmitCalls = 0;
            var verifiedRuntime = NativeSubmitRuntime.CreateTest(
                verifiedHook,
                new NativeSubmitInterceptionController(
                    verifiedProfile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                    activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
                    firstRunSetupController: new FirstRunSetupController(),
                    setupLayout: layout),
                () =>
                {
                    verifiedSubmitCalls++;
                    return CreateSubmittedResult("codex-desktop");
                },
                verifiedProfile);

            Assert.That(tray.ReloadNativeSubmit(verifiedRuntime), Is.True);
            verifiedHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

            Assert.That(verifiedHook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
            Assert.That(verifiedSubmitCalls, Is.EqualTo(1));
            Assert.That(tray.State.ProtectedSendBinding, Is.EqualTo("Ctrl+Enter"));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void TrayProtectionController_RoutesProtectedSendToTheActiveSelectedProfile()
    {
        var hook = new FakeNativeSubmitHookHost();
        var codexProfile = CreateProtectedProfile() with { ProfileId = "codex-desktop" };
        var chatGptProfile = CreateVerifiedChatGptProfile();
        var codexCalls = 0;
        var chatGptCalls = 0;
        var runtimes = new[]
        {
                NativeSubmitRuntime.CreateTest(
                hook,
                new NativeSubmitInterceptionController(codexProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                    activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop"))),
                () =>
                {
                    codexCalls++;
                    return CreateSubmittedResult("codex-desktop");
                },
                codexProfile),
            NativeSubmitRuntime.CreateTest(
                hook,
                new NativeSubmitInterceptionController(chatGptProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                    activeSurfaceDiscovery: CreateVerifiedChatGptDiscovery),
                () =>
                {
                    chatGptCalls++;
                    return CreateSubmittedResult("chatgpt-desktop");
                },
                chatGptProfile)
        };
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtimes[0].Controller,
            runtimes[0].Profile,
            nativeSubmitRuntimes: runtimes,
            activeSurfaceDiscovery: CreateVerifiedChatGptDiscovery);

        controller.Start();
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(codexCalls, Is.EqualTo(0));
        Assert.That(chatGptCalls, Is.EqualTo(1));
        Assert.That(controller.State.LastProfileId, Is.EqualTo("chatgpt-desktop"));
    }

    [Test]
    public void TrayProtectionController_KeepsVerifiedChatGptGuardedWhenCodexSetupIsIncomplete()
    {
        var hook = new FakeNativeSubmitHookHost();
        var codexProfile = CreateProtectedProfile() with
        {
            ProfileId = "codex-desktop",
            BindingSource = "not_verified",
            CapabilityStatus = OsInteractionStatusIds.BindingUnknown
        };
        var chatGptProfile = CreateVerifiedChatGptProfile();
        var chatGptCalls = 0;
        var runtimes = new[]
        {
            NativeSubmitRuntime.CreateTest(
                hook,
                new NativeSubmitInterceptionController(codexProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () => CreateSubmittedResult("codex-desktop"),
                codexProfile),
            NativeSubmitRuntime.CreateTest(
                hook,
                new NativeSubmitInterceptionController(
                    chatGptProfile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                    activeSurfaceDiscovery: CreateVerifiedChatGptDiscovery),
                () =>
                {
                    chatGptCalls++;
                    return CreateSubmittedResult("chatgpt-desktop");
                },
                chatGptProfile)
        };
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            runtimes[0].Controller,
            runtimes[0].Profile,
            nativeSubmitRuntimes: runtimes,
            activeSurfaceDiscovery: CreateVerifiedChatGptDiscovery);

        Assert.That(controller.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(hook.LastClassification?.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(chatGptCalls, Is.EqualTo(1));
        Assert.That(controller.State.LastProfileId, Is.EqualTo("chatgpt-desktop"));
    }

    [Test]
    public void WindowsTrayApp_ResolvesEveryEnabledPersistedProfileForNativeProtection()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(tempDirectory);
            var codexProfile = CreateProtectedProfile() with { ProfileId = "codex-desktop" };
            var chatGptProfile = CreateProtectedProfile() with { ProfileId = "chatgpt-desktop" };
            Assert.That(SubmitBindingProfileStore.Save(layout, new[] { codexProfile, chatGptProfile }).Succeeded, Is.True);

            var profiles = WindowsTrayApp.ResolveNativeProfilesForProtection(layout);

            Assert.That(profiles.Select(profile => profile.ProfileId), Is.EquivalentTo(new[] { "codex-desktop", "chatgpt-desktop" }));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Test]
    public void TrayProtectionController_ReloadKeepsUsingPublishedSnapshotUntilCandidateIsPublished()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var candidateHook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var oldRunnerCalls = 0;
        var candidateRunnerCalls = 0;
        string? oldRuntimeStatus = null;
        TrayProtectionController? controller = null;
        candidateHook.OnStarted = hook =>
        {
            oldHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
            hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
            oldRuntimeStatus = controller!.State.LastStatus;
        };
        controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                oldRunnerCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            profile);
        var candidateRuntime = NativeSubmitRuntime.CreateTest(
            candidateHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                candidateRunnerCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            profile);

        controller.Start();

        var reloaded = controller.ReloadNativeSubmit(candidateRuntime);

        Assert.That(reloaded, Is.True);
        Assert.That(oldRunnerCalls, Is.EqualTo(1));
        Assert.That(candidateRunnerCalls, Is.EqualTo(0));
        Assert.That(controller.State.LastStatus, Is.EqualTo(oldRuntimeStatus));
        candidateHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(candidateRunnerCalls, Is.EqualTo(1));
    }

    [Test]
    public void TrayProtectionController_FailedReloadKeepsPublishedSnapshotAndTrayState()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var failedHook = new FakeNativeSubmitHookHost { StartResult = false };
        var profile = CreateProtectedProfile();
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        controller.Start();
        var before = controller.GetCurrentSnapshot();
        var failedRuntime = NativeSubmitRuntime.CreateTest(
            failedHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);

        var reloaded = controller.ReloadNativeSubmit(failedRuntime);

        Assert.That(reloaded, Is.False);
        Assert.That(controller.GetCurrentSnapshot(), Is.SameAs(before));
        Assert.That(controller.State, Is.EqualTo(before.State));
        Assert.That(oldHook.Started, Is.True);
        Assert.That(failedHook.Started, Is.False);
    }

    [Test]
    public void ChatGptFingerprint_IsPublishedBeforeCandidateProfilePersistence()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var candidateHook = new FakeNativeSubmitHookHost();
        var oldFingerprint = new SurfaceCompatibilityEvidence(
            "old-app", "old-package", "old-version", "old-exe", "old-process", "old-window",
            "Chrome", "Group", "old-composer", "old-verification", DateTimeOffset.UtcNow,
            "Ctrl+Enter", "Enter", "old-send");
        var candidateFingerprint = oldFingerprint with
        {
            PackageVersion = "candidate-version",
            VerificationId = "candidate-verification"
        };
        var oldProfile = CreateProtectedProfile() with
        {
            ProfileId = "chatgpt-desktop",
            CompatibilityEvidence = oldFingerprint
        };
        var candidateProfile = oldProfile with { CompatibilityEvidence = candidateFingerprint };
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(oldProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(oldProfile.ProfileId),
            oldProfile);
        var candidateRuntime = NativeSubmitRuntime.CreateTest(
            candidateHook,
            new NativeSubmitInterceptionController(candidateProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(candidateProfile.ProfileId),
            candidateProfile);

        Assert.That(controller.Start(), Is.True);
        Assert.That(controller.ReloadNativeSubmit(candidateRuntime), Is.True);
        Assert.That(
            controller.GetCurrentSnapshot().RuntimeSet!.Runtimes.Single().Profile.CompatibilityEvidence,
            Is.EqualTo(candidateFingerprint));
    }

    [Test]
    public void ChatGptFingerprint_FailedCandidateActivationPreservesPriorPublishedFingerprint()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var failedHook = new FakeNativeSubmitHookHost { StartResult = false };
        var oldFingerprint = new SurfaceCompatibilityEvidence(
            "old-app", "old-package", "old-version", "old-exe", "old-process", "old-window",
            "Chrome", "Group", "old-composer", "old-verification", DateTimeOffset.UtcNow,
            "Ctrl+Enter", "Enter", "old-send");
        var candidateFingerprint = oldFingerprint with { VerificationId = "candidate-verification" };
        var oldProfile = CreateProtectedProfile() with
        {
            ProfileId = "chatgpt-desktop",
            CompatibilityEvidence = oldFingerprint
        };
        var candidateProfile = oldProfile with { CompatibilityEvidence = candidateFingerprint };
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(oldProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(oldProfile.ProfileId),
            oldProfile);
        var failedRuntime = NativeSubmitRuntime.CreateTest(
            failedHook,
            new NativeSubmitInterceptionController(candidateProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(candidateProfile.ProfileId),
            candidateProfile);

        Assert.That(controller.Start(), Is.True);
        var before = controller.GetCurrentSnapshot();
        Assert.That(controller.ReloadNativeSubmit(failedRuntime), Is.False);
        Assert.That(controller.GetCurrentSnapshot(), Is.SameAs(before));
        Assert.That(
            controller.GetCurrentSnapshot().RuntimeSet!.Runtimes.Single().Profile.CompatibilityEvidence,
            Is.EqualTo(oldFingerprint));
    }

    [Test]
    public void TrayProtectionController_DoesNotPublishRuntimeWhenResidentGateIsDisabled()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var candidateHook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var before = controller.GetCurrentSnapshot();
        var candidateRuntime = NativeSubmitRuntime.CreateTest(
            candidateHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);

        Assert.That(controller.ReloadNativeSubmit(candidateRuntime), Is.False);
        Assert.That(controller.GetCurrentSnapshot(), Is.SameAs(before));
        Assert.That(candidateHook.Started, Is.False);
        Assert.That(controller.IsNativeSubmitHookReady, Is.False);
    }

    [Test]
    public void TrayProtectionController_RuntimeReloadDoesNotOverwriteNewerProjectFileState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-reload-tests", Guid.NewGuid().ToString("N"));
        var layout = DefaultStorageLayout.Create(Path.Combine(directory, "data"));
        var workspace = Path.Combine(directory, "workspace");
        Directory.CreateDirectory(workspace);

        try
        {
            var oldHook = new FakeNativeSubmitHookHost();
            var candidateHook = new FakeNativeSubmitHookHost();
            var profile = CreateProtectedProfile();
            TrayProtectionController? controller = null;
            candidateHook.OnStarted = _ =>
            {
                ProtectedWorkspaceStore.Protect(layout, workspace);
                controller!.RefreshProjectFileProtectionStatus();
            };
        controller = TrayProtectionController.CreateTest(
                new FakeTrayHotkeyHost(),
                () => throw new InvalidOperationException("Manual scan should not run."),
                oldHook,
                new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () => CreateSubmittedResult(profile.ProfileId),
                profile,
                storageLayout: layout);
            var candidateRuntime = NativeSubmitRuntime.CreateTest(
                candidateHook,
                new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
                () => CreateSubmittedResult(profile.ProfileId),
                profile);

            Assert.That(controller.Start(), Is.True);
            Assert.That(controller.State.ProjectFileStatus, Is.EqualTo(ProjectFileProtectionStatusValues.NotConfigured));

            Assert.That(controller.ReloadNativeSubmit(candidateRuntime), Is.True);
            Assert.That(controller.State.ProjectFileStatus, Is.EqualTo(ProjectFileProtectionStatusValues.BrokerDemoOnly));
            Assert.That(candidateHook.Started, Is.True);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void TrayProtectionController_ReloadPublishesSnapshotWithMatchingTrayState()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var candidateHook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        controller.Start();
        var candidateRuntime = NativeSubmitRuntime.CreateTest(
            candidateHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);

        Assert.That(controller.ReloadNativeSubmit(candidateRuntime), Is.True);

        Assert.That(controller.GetCurrentSnapshot().State, Is.EqualTo(controller.State));
        Assert.That(controller.GetCurrentSnapshot().Generation, Is.EqualTo(1));
    }

    [Test]
    public void TrayProtectionController_ReloadResidentRuntimeSwapsApplyOnlyAndNativeSubmitTogether()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var candidateHook = new FakeNativeSubmitHookHost();
        var hotkey = new FakeTrayHotkeyHost();
        var profile = CreateProtectedProfile();
        var oldApplyCalls = 0;
        var candidateApplyCalls = 0;
        var candidateSubmitCalls = 0;
        var controller = TrayProtectionController.CreateTest(
            hotkey,
            () =>
            {
                oldApplyCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var candidateRuntime = NativeSubmitRuntime.CreateTest(
            candidateHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                candidateSubmitCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            profile);
        var replacement = new ResidentProtectionRuntime(
            () =>
            {
                candidateApplyCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            new NativeSubmitRuntimeSet(candidateHook, new[] { candidateRuntime }));

        controller.Start();

        Assert.That(controller.ReloadResidentRuntime(replacement), Is.True);

        hotkey.Trigger();
        candidateHook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(oldApplyCalls, Is.EqualTo(0));
        Assert.That(candidateApplyCalls, Is.EqualTo(1));
        Assert.That(candidateSubmitCalls, Is.EqualTo(1));
        Assert.That(oldHook.Started, Is.False);
        Assert.That(controller.GetCurrentSnapshot().Generation, Is.EqualTo(1));
    }

    [Test]
    public void TrayProtectionController_FailedResidentRuntimeReloadRetainsTheExistingBlockedRuntime()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var failedHook = new FakeNativeSubmitHookHost { StartResult = false };
        var hotkey = new FakeTrayHotkeyHost();
        var profile = CreateProtectedProfile();
        var oldApplyCalls = 0;
        var candidateApplyCalls = 0;
        var controller = TrayProtectionController.CreateTest(
            hotkey,
            () =>
            {
                oldApplyCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var failedRuntime = NativeSubmitRuntime.CreateTest(
            failedHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var replacement = new ResidentProtectionRuntime(
            () =>
            {
                candidateApplyCalls++;
                return CreateSubmittedResult(profile.ProfileId);
            },
            new NativeSubmitRuntimeSet(failedHook, new[] { failedRuntime }));

        controller.Start();

        Assert.That(controller.ReloadResidentRuntime(replacement), Is.False);

        hotkey.Trigger();

        Assert.That(oldApplyCalls, Is.EqualTo(1));
        Assert.That(candidateApplyCalls, Is.EqualTo(0));
        Assert.That(oldHook.Started, Is.True);
        Assert.That(controller.GetCurrentSnapshot().Generation, Is.EqualTo(0));
    }

    [Test]
    public void TrayProtectionController_InFlightApplyOnlyCannotOverwriteAResidentRuntimeReplacement()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var candidateHook = new FakeNativeSubmitHookHost();
        var hotkey = new FakeTrayHotkeyHost();
        var profile = CreateProtectedProfile();
        using var applyStarted = new ManualResetEventSlim(false);
        using var releaseApply = new ManualResetEventSlim(false);
        var controller = TrayProtectionController.CreateTest(
            hotkey,
            () =>
            {
                applyStarted.Set();
                releaseApply.Wait();
                return CreateSubmittedResult(profile.ProfileId);
            },
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var candidateRuntime = NativeSubmitRuntime.CreateTest(
            candidateHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var replacement = new ResidentProtectionRuntime(
            () => CreateSubmittedResult(profile.ProfileId),
            new NativeSubmitRuntimeSet(candidateHook, new[] { candidateRuntime }));

        controller.Start();
        var applyTask = System.Threading.Tasks.Task.Run(hotkey.Trigger);
        Assert.That(applyStarted.Wait(TimeSpan.FromSeconds(2)), Is.True);

        Assert.That(controller.ReloadResidentRuntime(replacement), Is.True);
        releaseApply.Set();
        applyTask.Wait();

        Assert.That(controller.GetCurrentSnapshot().Generation, Is.EqualTo(1));
        Assert.That(controller.GetCurrentSnapshot().State.LastStatus, Is.EqualTo("native_submit_runtime_reloaded"));
        Assert.That(candidateHook.Started, Is.True);
    }

    [Test]
    public void TrayProtectionController_ConcurrentReloadRequestsPublishWholeGenerations()
    {
        var oldHook = new FakeNativeSubmitHookHost();
        var firstCandidateHook = new FakeNativeSubmitHookHost();
        var secondCandidateHook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            oldHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        controller.Start();
        var firstRuntime = NativeSubmitRuntime.CreateTest(
            firstCandidateHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        var secondRuntime = NativeSubmitRuntime.CreateTest(
            secondCandidateHook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => CreateSubmittedResult(profile.ProfileId),
            profile);
        using var start = new ManualResetEventSlim(false);

        var firstReload = System.Threading.Tasks.Task.Run(() =>
        {
            start.Wait();
            return controller.ReloadNativeSubmit(firstRuntime);
        });
        var secondReload = System.Threading.Tasks.Task.Run(() =>
        {
            start.Wait();
            return controller.ReloadNativeSubmit(secondRuntime);
        });
        start.Set();
        System.Threading.Tasks.Task.WaitAll(firstReload, secondReload);

        Assert.That(firstReload.Result, Is.True);
        Assert.That(secondReload.Result, Is.True);
        Assert.That(controller.GetCurrentSnapshot().Generation, Is.EqualTo(2));
        Assert.That(controller.GetCurrentSnapshot().State, Is.EqualTo(controller.State));
        Assert.That(oldHook.Started, Is.False);
        Assert.That(firstCandidateHook.Started ^ secondCandidateHook.Started, Is.True);
    }

    [Test]
    public void HandleButtonClick_MatchesProfileAndAllowsSubmit()
    {
        var profile = CreateProtectedProfile();
        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        var activeSurface = TestSurfaceFactory.CreateTestSurface(
            "codex-desktop",
            composerStatus: OsInteractionStatusIds.SupportedComposer);

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

        var activeSurface = TestSurfaceFactory.CreateTestSurface(
            "codex-desktop",
            composerStatus: "unsupported_composer");

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
            Metadata: new SurfaceMetadata(ComposerStatus: OsInteractionStatusIds.SupportedComposer));

        var result = controller.HandleButtonClick(activeSurface);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Applied, Is.False);
        Assert.That(result.Submitted, Is.False);
    }

    [Test]
    public void HandleButtonClick_SuppressesUnprotectedProfile()
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

        var activeSurface = TestSurfaceFactory.CreateTestSurface("codex-desktop");

        var result = controller.HandleButtonClick(activeSurface);

        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.BindingUnknown));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void HandleButtonClick_UsesSubmitBindingFromProfile()
    {
        // Test with Enter as Send / Ctrl+Enter as newline
        var profile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        var activeSurface = TestSurfaceFactory.CreateTestSurface("codex-desktop");

        var result = controller.HandleButtonClick(activeSurface);

        // Enter should be guarded (matches SubmitBinding)
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void HandleButtonClick_UsesSubmitBindingCtrlEnterFromProfile()
    {
        // Test with Ctrl+Enter as Send / Enter as newline
        var profile = CreateVerifiedChatGptProfile();

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: CreateVerifiedChatGptDiscovery,
            firstRunSetupController: null);

        var activeSurface = TestSurfaceFactory.CreateTestSurface("chatgpt-desktop");

        var result = controller.HandleButtonClick(activeSurface);

        // Ctrl+Enter should be guarded (matches SubmitBinding)
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(result.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void HandleButtonClick_SkipsDisabledProfile()
    {
        // Test with disabled profile (Enabled: false)
        var profile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: false,  // Disabled
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        var activeSurface = TestSurfaceFactory.CreateTestSurface("codex-desktop");

        var result = controller.HandleButtonClick(activeSurface);

        // Disabled profile should pass through (not guarded)
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Diagnostics["enabled"], Is.EqualTo("false"));
    }

    [Test]
    public void HandleGesture_SkipsDisabledProfile()
    {
        // Test with disabled profile (Enabled: false)
        var profile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: false,  // Disabled
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")),
            firstRunSetupController: null);

        // Try to trigger the submit gesture (Enter matching SubmitBinding)
        var result = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: false));

        // Disabled profile should pass through (not guarded)
        Assert.That(result.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(result.SuppressOriginalInput, Is.False);
        Assert.That(result.Diagnostics["enabled"], Is.EqualTo("false"));
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
            var profile = CreateVerifiedChatGptProfile();

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

[TestFixture]
public class NativeSubmitBindingScopeTests : SanitizerTests
{
    [Test]
    public void NativeSubmitBindingScope_EnterAsSend_CtrlEnterAsNewline()
    {
        // With Enter configured as Send, Ctrl+Enter passes through as newline
        var profile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));

        // Ctrl+Enter should pass through as newline
        var ctrlEnter = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(ctrlEnter.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(ctrlEnter.SuppressOriginalInput, Is.False);
        Assert.That(ctrlEnter.Diagnostics["pass_through_reason"], Is.EqualTo("newline_binding"));

        // Enter should be guarded at verified composer
        var enter = controller.HandleGesture(new NativeKeyGesture("Enter"));
        Assert.That(enter.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(enter.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void NativeSubmitBindingScope_CtrlEnterAsSend_EnterAsNewline()
    {
        // With Ctrl+Enter configured as Send, ordinary Enter passes through as newline
        var profile = CreateVerifiedChatGptProfile();

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: CreateVerifiedChatGptDiscovery);

        // Ordinary Enter should pass through as newline (matches NewlineBinding)
        var enter = controller.HandleGesture(new NativeKeyGesture("Enter"));
        Assert.That(enter.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(enter.SuppressOriginalInput, Is.False);
        Assert.That(enter.Diagnostics["pass_through_reason"], Is.EqualTo("newline_binding"));

        // Ctrl+Enter should be guarded at verified composer (matches SubmitBinding)
        var ctrlEnter = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(ctrlEnter.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));
        Assert.That(ctrlEnter.SuppressOriginalInput, Is.True);
    }

    [Test]
    public void NativeSubmitBindingScope_UnrelatedKeysPassThrough()
    {
        // A failed surface read or non-Send control does not turn ordinary typing into a global fail-closed condition
        var profile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string>
                {
                    ["profile_id"] = "codex-desktop",
                    ["composer_status"] = OsInteractionStatusIds.NotComposer
                }));

        // Unrelated key (A) should pass through
        var aKey = controller.HandleGesture(new NativeKeyGesture("A"));
        Assert.That(aKey.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(aKey.SuppressOriginalInput, Is.False);

        // Enter should fail closed for non-composer
        var enter = controller.HandleGesture(new NativeKeyGesture("Enter"));
        Assert.That(enter.Status, Is.EqualTo(OsInteractionStatusIds.NotComposer));
        Assert.That(enter.SuppressOriginalInput, Is.True);
        Assert.That(enter.Diagnostics["fail_closed_reason"], Is.EqualTo("selected_profile_not_composer"));
    }

    [Test]
    public void NativeSubmitBindingScope_SelectedVsUnselectedApps()
    {
        // Selected app: Enter is guarded, Ctrl+Enter passes through as newline
        var profile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var selectedController = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));

        var unselectedController = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("other-app")));

        // In selected app, Enter is guarded
        var selectedEnter = selectedController.HandleGesture(new NativeKeyGesture("Enter"));
        Assert.That(selectedEnter.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));

        // In unselected app, Enter passes through (profile mismatch)
        var unselectedEnter = unselectedController.HandleGesture(new NativeKeyGesture("Enter"));
        Assert.That(unselectedEnter.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
        Assert.That(unselectedEnter.Diagnostics["pass_through_reason"], Is.EqualTo("active_profile_mismatch"));
    }

    [Test]
    public void NativeSubmitBindingScope_RepeatedAttempts()
    {
        // Same behavior for repeated attempts with same profile
        var profile = new SubmitBindingProfile(
            "codex-desktop",
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: SubmitKeyBinding.Parse("Enter").Binding!,
            NewlineBinding: SubmitKeyBinding.Parse("Ctrl+Enter").Binding!,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());

        var controller = new NativeSubmitInterceptionController(
            profile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(CreateNativeSubmitSurface("codex-desktop")));

        // First attempt
        var enter1 = controller.HandleGesture(new NativeKeyGesture("Enter"));
        Assert.That(enter1.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));

        // Second attempt should have same behavior
        var enter2 = controller.HandleGesture(new NativeKeyGesture("Enter"));
        Assert.That(enter2.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitGuarded));

        // Ctrl+Enter should pass through
        var ctrlEnter = controller.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        Assert.That(ctrlEnter.Status, Is.EqualTo(OsInteractionStatusIds.NativeSubmitPassThrough));
    }

    [Test]
    public void SurfaceMetadata_CreatesDictionaryWithNamedFields()
    {
        var metadata = new SurfaceMetadata(
            SurfaceKind: "disposable_local_target",
            CloudSubmission: "false",
            ComposerStatus: OsInteractionStatusIds.SupportedComposer);

        var dict = metadata.ToDictionary();

        Assert.That(dict, Has.Count.EqualTo(3));
        Assert.That(dict["surface_kind"], Is.EqualTo("disposable_local_target"));
        Assert.That(dict["cloud_submission"], Is.EqualTo("false"));
        Assert.That(dict["composer_status"], Is.EqualTo(OsInteractionStatusIds.SupportedComposer));
    }

    [Test]
    public void SurfaceMetadata_HandlesNullFields()
    {
        var metadata = new SurfaceMetadata(CloudSubmission: "true");

        var dict = metadata.ToDictionary();

        Assert.That(dict, Has.Count.EqualTo(1));
        Assert.That(dict["cloud_submission"], Is.EqualTo("true"));
    }

    [Test]
    public void SurfaceMetadata_FromDictionary_RoundTrip()
    {
        var original = new SurfaceMetadata(
            SurfaceKind: "disposable_local_target",
            CloudSubmission: "false",
            ComposerStatus: OsInteractionStatusIds.SupportedComposer);

        var dict = original.ToDictionary();
        var restored = SurfaceMetadata.FromDictionary(dict);

        Assert.That(restored.SurfaceKind, Is.EqualTo(original.SurfaceKind));
        Assert.That(restored.CloudSubmission, Is.EqualTo(original.CloudSubmission));
        Assert.That(restored.ComposerStatus, Is.EqualTo(original.ComposerStatus));
    }

    [Test]
    public void SurfaceMetadata_FromDictionary_IgnoresUnknownKeys()
    {
        var dict = new Dictionary<string, string>
        {
            ["surface_kind"] = "test",
            ["unknown_key"] = "unknown_value",
            ["cloud_submission"] = "true"
        };

        var metadata = SurfaceMetadata.FromDictionary(dict);

        Assert.That(metadata.SurfaceKind, Is.EqualTo("test"));
        Assert.That(metadata.CloudSubmission, Is.EqualTo("true"));
        Assert.That(metadata.ComposerStatus, Is.Null);
    }

    [Test]
    public void ReferenceComposerAcceptance_SafePromptReplaysThroughResidentHookPath()
    {
        var report = ReferenceComposerAcceptanceRunner.Run(
            new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("reference-composer-test-secret"))),
            "A harmless local prompt",
            ReferenceComposerDecision.Approve);

        Assert.That(report.HookStarted, Is.True);
        Assert.That(report.OriginalInputSuppressed, Is.True);
        Assert.That(report.TerminalStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(report.Submitted, Is.True);
        Assert.That(report.SentTexts, Is.EqualTo(new[] { "A harmless local prompt" }));
        Assert.That(report.Trace.Select(entry => entry.Stage), Is.EqualTo(new[]
        {
            "send_detected",
            "target_matched",
            "composer_read",
            "sanitized",
            "send_injected",
            "sent_safely"
        }));
    }

    [Test]
    public void ReferenceComposerAcceptance_SensitivePromptUsesProductionOverlayAndNeverSendsRawText()
    {
        const string sensitivePrompt = "Connect to 192.168.10.25";
        var report = ReferenceComposerAcceptanceRunner.Run(
            new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("reference-composer-test-secret"))),
            sensitivePrompt,
            ReferenceComposerDecision.Approve);

        Assert.That(report.HookStarted, Is.True);
        Assert.That(report.OriginalInputSuppressed, Is.True);
        Assert.That(report.TerminalStatus, Is.EqualTo(OsInteractionStatusIds.Submitted));
        Assert.That(report.Submitted, Is.True);
        Assert.That(report.SentTexts, Has.Count.EqualTo(1));
        Assert.That(report.SentTexts[0], Does.Not.Contain("192.168.10.25"));
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Contain("overlay_created"));
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Contain("overlay_foreground_confirmed"));
        Assert.That(report.Trace[^1].Stage, Is.EqualTo("sent_safely"));
    }

    [Test]
    public void ReferenceComposerAcceptance_ForegroundRefusalBlocksSuppressedSend()
    {
        var report = ReferenceComposerAcceptanceRunner.Run(
            new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("reference-composer-test-secret"))),
            "Connect to 192.168.10.25",
            ReferenceComposerDecision.Approve,
            ReferenceComposerForegroundMode.Refused);

        Assert.That(report.HookStarted, Is.True);
        Assert.That(report.OriginalInputSuppressed, Is.True);
        Assert.That(report.Submitted, Is.False);
        Assert.That(report.SentTexts, Is.Empty);
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Not.Contain("overlay_foreground_confirmed"));
        Assert.That(report.Trace[^1].Stage, Is.EqualTo("terminal_blocked"));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void ReferenceComposerAcceptance_TargetChangeBlocksBeforeSideEffect(int mode)
    {
        var report = ReferenceComposerAcceptanceRunner.Run(
            new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("reference-composer-test-secret"))),
            "Connect to 192.168.10.25",
            ReferenceComposerDecision.Approve,
            ReferenceComposerForegroundMode.Verified,
            (ReferenceComposerTargetChangeMode)mode);

        Assert.That(report.HookStarted, Is.True);
        Assert.That(report.OriginalInputSuppressed, Is.True);
        Assert.That(report.Submitted, Is.False);
        Assert.That(report.SentTexts, Is.Empty);
        Assert.That(report.Trace[^1].Stage, Is.EqualTo("terminal_blocked"));
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Not.Contain("sent_safely"));
    }

    [Test]
    public void ReferenceComposerAcceptance_UiAutomationWriteFailureBlocksAfterApproval()
    {
        var report = ReferenceComposerAcceptanceRunner.Run(
            new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("reference-composer-test-secret"))),
            "Connect to 192.168.10.25",
            ReferenceComposerDecision.Approve,
            ReferenceComposerForegroundMode.Verified,
            ReferenceComposerTargetChangeMode.None,
            ReferenceComposerWriteMode.Unavailable);

        Assert.That(report.HookStarted, Is.True);
        Assert.That(report.OriginalInputSuppressed, Is.True);
        Assert.That(report.Submitted, Is.False);
        Assert.That(report.SentTexts, Is.Empty);
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Not.Contain("text_written"));
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Not.Contain("sent_safely"));
        Assert.That(report.Trace[^1].Stage, Is.EqualTo("terminal_blocked"));
    }

    [TestCase(1)]
    [TestCase(2)]
    public void ReferenceComposerAcceptance_ReplayFailureBlocksWithoutSuccessTrace(int replayModeValue)
    {
        var replayMode = (ReferenceComposerReplayMode)replayModeValue;
        var report = ReferenceComposerAcceptanceRunner.Run(
            new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("reference-composer-test-secret"))),
            "Connect to 192.168.10.25",
            ReferenceComposerDecision.Approve,
            ReferenceComposerForegroundMode.Verified,
            ReferenceComposerTargetChangeMode.None,
            ReferenceComposerWriteMode.Available,
            replayMode);

        Assert.That(report.HookStarted, Is.True);
        Assert.That(report.OriginalInputSuppressed, Is.True);
        Assert.That(report.Submitted, Is.False);
        Assert.That(report.SentTexts, Is.Empty);
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Not.Contain("send_injected"));
        Assert.That(report.Trace.Select(entry => entry.Stage), Does.Not.Contain("sent_safely"));
        Assert.That(report.Trace[^1].Stage, Is.EqualTo("terminal_blocked"));
        Assert.That(report.Trace[^1].ResultCode, Is.EqualTo(OsInteractionStatusIds.ReplayIndeterminate));
        Assert.That(report.ReplayDiagnostics["replay_outcome"], Is.EqualTo(replayMode == ReferenceComposerReplayMode.Partial ? "partial" : "unavailable"));
        Assert.That(report.ReplayDiagnostics["modifiers_released"], Is.EqualTo("true"));
    }

    [Test]
    public void ReferenceComposerAcceptance_CancelAndRepeatedRunLeaveNoSendOrLeakedCapability()
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("reference-composer-test-secret")));
        var cancelled = ReferenceComposerAcceptanceRunner.Run(
            sanitizer,
            "Connect to 192.168.10.25",
            ReferenceComposerDecision.Cancel);
        var repeated = ReferenceComposerAcceptanceRunner.Run(
            sanitizer,
            "A harmless local prompt",
            ReferenceComposerDecision.Approve);

        Assert.That(cancelled.Submitted, Is.False);
        Assert.That(cancelled.SentTexts, Is.Empty);
        Assert.That(cancelled.Trace[^1].Stage, Is.EqualTo("terminal_blocked"));
        Assert.That(repeated.Submitted, Is.True);
        Assert.That(repeated.SentTexts, Has.Count.EqualTo(1));
    }

    [Test]
    public void ReferenceComposerReleaseAcceptance_RunsFullMatrixTwice()
    {
        var report = ReferenceComposerReleaseAcceptanceRunner.Run(
            System.Text.Encoding.UTF8.GetBytes("reference-composer-release-test-secret"),
            interactiveDesktopProbe: () => true);

        Assert.That(report.Passed, Is.True);
        Assert.That(report.Status, Is.EqualTo("passed"));
        Assert.That(report.InteractiveDesktopAvailable, Is.True);
        Assert.That(report.CleanupPassed, Is.True);
        Assert.That(report.Scenarios, Has.Count.EqualTo(18));
        Assert.That(report.Scenarios.All(scenario => scenario.Passed && scenario.RawFree && scenario.CleanupPassed), Is.True);
        Assert.That(report.Scenarios.Select(scenario => scenario.ScenarioId).Count(id => id.StartsWith("run1.", StringComparison.Ordinal)), Is.EqualTo(9));
        Assert.That(report.Scenarios.Select(scenario => scenario.ScenarioId).Count(id => id.StartsWith("run2.", StringComparison.Ordinal)), Is.EqualTo(9));
    }

    [Test]
    public void ReferenceComposerReleaseAcceptance_NonInteractiveIsExplicitAndFailClosed()
    {
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var exitCode = Program.RunReferenceComposerReleaseAcceptance(() => false);

            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(output.ToString(), Does.Contain("status: interactive_desktop_unavailable"));
            Assert.That(output.ToString(), Does.Contain("overall: failed_closed"));
            Assert.That(output.ToString(), Does.Not.Contain("reference-composer-release-acceptance-secret"));
            Assert.That(output.ToString(), Does.Not.Contain("protected"));
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [Test]
    public void TrayProtectionController_PublishesKnownRawFreePipelineFailureCode()
    {
        var hook = new FakeNativeSubmitHookHost();
        var profile = CreateProtectedProfile();
        var controller = TrayProtectionController.CreateTest(
            new FakeTrayHotkeyHost(),
            () => throw new InvalidOperationException("Manual scan should not run."),
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => new OsInteractionResult(
                OsInteractionStatusIds.FailedClosed,
                CreateNativeSubmitSurface("codex-desktop"),
                null,
                null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["exception_status"] = "orchestrator_failure",
                    ["exception_type"] = "test.secret.com"
                }),
            profile);

        Assert.That(controller.Start(), Is.True);
        hook.Trigger(new NativeKeyGesture("Enter", Ctrl: true));

        Assert.That(controller.State.ProtectedSendAttemptStatus, Is.EqualTo("protection_unavailable"));
        Assert.That(controller.State.LastProtectedSendFailureCode, Is.EqualTo("orchestrator_failure"));
        Assert.That(controller.State.LastProtectedSendFailureCode, Does.Not.Contain("test.secret.com"));
    }

    [Test]
    public void ReferenceComposerAcceptance_ReleaseSmokeRunsAllAcceptanceCases()
    {
        var report = ReferenceComposerAcceptanceSmokeRunner.Run(
            System.Text.Encoding.UTF8.GetBytes("reference-composer-release-smoke-secret"));

        Assert.That(report.SafePromptPassed, Is.True);
        Assert.That(report.SensitivePromptPassed, Is.True);
        Assert.That(report.CancellationPassed, Is.True);
        Assert.That(report.RepeatedCleanupPassed, Is.True);
    }
}
