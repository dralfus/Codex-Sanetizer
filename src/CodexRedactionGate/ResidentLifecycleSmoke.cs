using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal sealed record ResidentLifecycleSmokeReport(
    bool HookRegistrationPassed,
    bool SetupGatePassed,
    bool RuntimeReloadPassed,
    bool RuntimeRollbackPassed,
    bool SelectedSendFailurePassed,
    bool RawFreeFailurePassed,
    bool TargetChangeAbortPassed,
    bool SecondInstancePassed,
    bool ProtectedStatusPassed,
    bool NoCloudSubmissionPassed)
{
    public bool Passed => HookRegistrationPassed
        && SetupGatePassed
        && RuntimeReloadPassed
        && RuntimeRollbackPassed
        && SelectedSendFailurePassed
        && RawFreeFailurePassed
        && TargetChangeAbortPassed
        && SecondInstancePassed
        && ProtectedStatusPassed
        && NoCloudSubmissionPassed;
}

internal static class ResidentLifecycleSmokeRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static ResidentLifecycleSmokeReport Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failed();
        }

        var directory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-resident-smoke", Guid.NewGuid().ToString("N"));
        ResidentLifecycleSmokeReport? report = null;
        var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() => report = RunOnStaThread(directory, completed))
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var joined = false;
        try
        {
            joined = thread.Join(Timeout);
            return joined && completed.IsSet && report is not null
                ? report
                : Failed();
        }
        finally
        {
            if (joined)
            {
                completed.Dispose();
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }

    private static ResidentLifecycleSmokeReport RunOnStaThread(string directory, ManualResetEventSlim completed)
    {
        var hookRegistrationPassed = false;
        var setupGatePassed = false;
        var runtimeReloadPassed = false;
        var runtimeRollbackPassed = false;
        var selectedSendFailurePassed = false;
        var rawFreeFailurePassed = false;
        var targetChangeAbortPassed = false;
        var secondInstancePassed = false;
        var protectedStatusPassed = false;
        var cloudSubmissionCount = 0;
        var layout = DefaultStorageLayout.Create(directory);
        _ = Sanitizer.CreateProduction(layout);
        var setup = new SmokeSetupController();
        var initialHook = new WindowsNativeSubmitHookHost();
        var reloadedHook = new WindowsNativeSubmitHookHost();

        try
        {
            var pendingProfile = FirstRunSetupController.CreateDefaultSetupProfile("codex-desktop")!;
            var protectedProfile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
                "codex-desktop",
                "Enter",
                "Ctrl+Enter",
                TextSurfaceDiscoveryResult.Success(SmokeSurfaceFactory.CreateSmokeNativeSubmitSurface("codex-desktop")));
            var initialController = new NativeSubmitInterceptionController(
                pendingProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                profileSnapshot: NativeSubmitProfileSnapshot.FromProfile(pendingProfile));
            var reloadedController = new NativeSubmitInterceptionController(
                protectedProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                profileSnapshot: NativeSubmitProfileSnapshot.FromProfile(protectedProfile));
            var protection = TrayProtectionController.CreateTest(
                new UnavailableTrayHotkeyHost(
                    new HotkeyBinding("resident-smoke", "Ctrl+Shift+F9", "resident_smoke"),
                    "resident_smoke_manual_hotkey_disabled"),
                NoCloudSubmission,
                initialHook,
                initialController,
                NoCloudSubmission,
                pendingProfile,
                storageLayout: layout);
            var instanceId = "resident-smoke-" + Guid.NewGuid().ToString("N");
            using var instance = new SingleInstanceEnforcement(instanceId);

            using var context = new WindowsTrayApplicationContext(
                protection,
                layout,
                new SmokeCommandLauncher(),
                new SmokeDisableConfirmation(),
                instance,
                () => new NativeSubmitRuntimeSet(
                    reloadedHook,
                    new[] { NativeSubmitRuntime.CreateTest(reloadedHook, reloadedController, NoCloudSubmission, protectedProfile) }),
                () => setup,
                backgroundWorkQueue: work => work(),
                firstRunSetupCompleted: _ =>
                {
                    var readinessPassed = protection.State.LocalReadinessStatus == "passed";
                    runtimeReloadPassed = reloadedHook.IsKeyboardHookRegistered
                        && !initialHook.IsKeyboardHookRegistered
                        && protection.State.NativeSubmitStatus == OsInteractionStatusIds.Protected
                        && protection.State.ComposerProtected == readinessPassed;
                    var rejectedCandidate = new UnavailableNativeSubmitHookHost("resident_smoke_candidate_unavailable");
                    runtimeRollbackPassed = !protection.ReloadNativeSubmit(new NativeSubmitRuntimeSet(
                        rejectedCandidate,
                        new[] { NativeSubmitRuntime.CreateTest(rejectedCandidate, reloadedController, NoCloudSubmission, protectedProfile) }))
                        && reloadedHook.IsKeyboardHookRegistered
                        && protection.State.NativeSubmitStatus == OsInteractionStatusIds.Protected
                        && protection.State.ComposerProtected == readinessPassed;
                    var selectedFailure = reloadedController.HandleIdentifiedSendControl(
                        TextSurfaceDiscoveryResult.Failure(
                            OsInteractionStatusIds.SurfaceUnverified,
                            new Dictionary<string, string>
                            {
                                ["profile_id"] = protectedProfile.ProfileId,
                                ["test_input"] = "RESIDENT_SMOKE_SENSITIVE_VALUE"
                            }));
                    selectedSendFailurePassed = selectedFailure.Status == OsInteractionStatusIds.SurfaceUnverified
                        && selectedFailure.SuppressOriginalInput
                        && !selectedFailure.Submitted;
                    rawFreeFailurePassed = !selectedFailure.Diagnostics.Values.Contains(
                        "RESIDENT_SMOKE_SENSITIVE_VALUE",
                        StringComparer.Ordinal);
                    targetChangeAbortPassed = TargetChangeAborts(protectedProfile);
                    using var second = new SingleInstanceEnforcement(instanceId);
                    secondInstancePassed = !second.IsFirstInstance;
                    var renderedStatus = TrayStatusFormatter.FormatMenuStatus(protection.State);
                    protectedStatusPassed = renderedStatus.Contains("protected_send_binding=Enter", StringComparison.Ordinal)
                        && renderedStatus.Contains("newline_binding=Ctrl+Enter", StringComparison.Ordinal)
                        && renderedStatus.Contains("manual_scan_hotkey=Ctrl+Shift+F9", StringComparison.Ordinal)
                        && renderedStatus.Contains(
                            $"composer_protected={readinessPassed.ToString().ToLowerInvariant()}",
                            StringComparison.Ordinal);
                    completed.Set();
                    Application.ExitThread();
                });

            hookRegistrationPassed = initialHook.IsKeyboardHookRegistered;
            var setupGate = initialController.HandleGesture(new NativeKeyGesture("Enter"));
            setupGatePassed = protection.State.SetupRequired
                && protection.State.NativeSubmitStatus == OsInteractionStatusIds.NativeSubmitSetupRequired
                && setupGate.Status == OsInteractionStatusIds.NativeSubmitSetupRequired
                && setupGate.SuppressOriginalInput;

            Application.Run(context);
        }
        catch
        {
            return Failed();
        }

        return new ResidentLifecycleSmokeReport(
            hookRegistrationPassed,
            setupGatePassed,
            runtimeReloadPassed,
            runtimeRollbackPassed,
            selectedSendFailurePassed,
            rawFreeFailurePassed,
            targetChangeAbortPassed,
            secondInstancePassed,
            protectedStatusPassed,
            Volatile.Read(ref cloudSubmissionCount) == 0);

        OsInteractionResult NoCloudSubmission()
        {
            Interlocked.Increment(ref cloudSubmissionCount);
            return new OsInteractionResult(
                OsInteractionStatusIds.FailedClosed,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>());
        }
    }

    private static ResidentLifecycleSmokeReport Failed()
    {
        return new ResidentLifecycleSmokeReport(false, false, false, false, false, false, false, false, false, false);
    }

    private static bool TargetChangeAborts(SubmitBindingProfile profile)
    {
        var captured = WithWindowHandle(SmokeSurfaceFactory.CreateSmokeNativeSubmitSurface(profile.ProfileId), "1");
        var changed = WithWindowHandle(SmokeSurfaceFactory.CreateSmokeNativeSubmitSurface(profile.ProfileId), "2");
        var hook = new ControlledNativeSubmitHookHost();
        var targetRunnerCalls = 0;
        var runtime = NativeSubmitRuntime.CreateTest(
            hook,
            new NativeSubmitInterceptionController(profile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => throw new InvalidOperationException("untargeted_runner_not_expected"),
            profile,
            ResidentTargetTracedRunner: (target, traceStage, executionGuard, executionLease) =>
            {
                foreach (var stage in new[]
                {
                    (Stage: "composer_read", Code: "capture_verified"),
                    (Stage: "sanitized", Code: "sanitization_verified"),
                    (Stage: "send_injected", Code: "submit_requested")
                })
                {
                    if (!traceStage(stage.Stage, stage.Code))
                    {
                        return new OsInteractionResult(
                            OsInteractionStatusIds.FailedClosed,
                            Surface: null,
                            SanitizationResult: null,
                            ConfirmationModel: null,
                            Applied: false,
                            Submitted: false,
                            Diagnostics: new Dictionary<string, string>
                            {
                                ["trace_status"] = "trace_unavailable"
                            });
                    }
                }

                if (!executionGuard())
                {
                    return new OsInteractionResult(
                        OsInteractionStatusIds.FailedClosed,
                        Surface: null,
                        SanitizationResult: null,
                        ConfirmationModel: null,
                        Applied: false,
                        Submitted: false,
                        Diagnostics: new Dictionary<string, string>
                        {
                            ["trace_status"] = "resident_operation_unavailable"
                        });
                }

                var lease = executionLease();
                if (lease is null)
                {
                    return new OsInteractionResult(
                        OsInteractionStatusIds.FailedClosed,
                        Surface: null,
                        SanitizationResult: null,
                        ConfirmationModel: null,
                        Applied: false,
                        Submitted: false,
                        Diagnostics: new Dictionary<string, string>
                        {
                            ["trace_status"] = "resident_operation_unavailable"
                        });
                }

                try
                {
                    targetRunnerCalls++;
                    var result = new CapturedTargetSurfaceDiscovery(
                        new StaticSurfaceDiscovery(changed),
                        target).DiscoverActiveSurface();
                    return new OsInteractionResult(
                        result.Status,
                        result.Surface,
                        SanitizationResult: null,
                        ConfirmationModel: null,
                        Applied: false,
                        Submitted: false,
                        result.Diagnostics);
                }
                finally
                {
                    lease.Dispose();
                }
            });
        var protection = new TrayProtectionController(
            new UnavailableTrayHotkeyHost(
                new HotkeyBinding("target-change-smoke", "Ctrl+Shift+F9", "target_change_smoke"),
                "target_change_manual_hotkey_disabled"),
            () => throw new InvalidOperationException("manual_scan_not_expected"),
            hook,
            runtime.Controller,
            profile,
            nativeSubmitRuntimes: new[] { runtime },
            activeSurfaceDiscovery: () => TextSurfaceDiscoveryResult.Success(captured));

        if (!protection.Start())
        {
            return false;
        }

        hook.Trigger(new NativeKeyGesture("Enter", TargetWindow: new IntPtr(1), TargetProcessId: 1));
        return targetRunnerCalls == 1
            && protection.State.LastStatus == OsInteractionStatusIds.StaleComposer
            && !protection.State.LastSubmitted;
    }

    private static TextSurfaceDescriptor WithWindowHandle(TextSurfaceDescriptor surface, string windowHandle)
    {
        return surface with { Metadata = surface.Metadata with { WindowHandle = windowHandle } };
    }

    private sealed class SmokeCommandLauncher : ITrayLocalCommandLauncher
    {
        public void Open(TrayLocalCommand command)
        {
        }
    }

    private sealed class SmokeDisableConfirmation : ITrayProtectionDisableConfirmation
    {
        public bool Confirm(string action, TrayProtectionState state) => true;
    }

    private sealed class StaticSurfaceDiscovery : IActiveTextSurfaceDiscovery
    {
        private readonly TextSurfaceDescriptor _surface;

        public StaticSurfaceDiscovery(TextSurfaceDescriptor surface)
        {
            _surface = surface;
        }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            return TextSurfaceDiscoveryResult.Success(_surface);
        }
    }

    private sealed class ControlledNativeSubmitHookHost : INativeSubmitHookHost
    {
        private Func<NativeKeyGesture, NativeSubmitInterceptionResult>? _classify;
        private Action<NativeKeyGesture, NativeSubmitInterceptionResult>? _onSuppressedSubmit;

        public string? LastErrorCode => null;

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture, NativeSubmitInterceptionResult> onSuppressedSubmit,
            Func<NativeKeyGesture, bool> shouldSuppressClassificationFailure)
        {
            _classify = classify ?? throw new ArgumentNullException(nameof(classify));
            _onSuppressedSubmit = onSuppressedSubmit ?? throw new ArgumentNullException(nameof(onSuppressedSubmit));
            return true;
        }

        public void Stop()
        {
            _classify = null;
            _onSuppressedSubmit = null;
        }

        public void Trigger(NativeKeyGesture gesture)
        {
            var result = _classify!(gesture);
            if (result.Status == OsInteractionStatusIds.NativeSubmitGuarded)
            {
                _onSuppressedSubmit!(gesture, result);
            }
        }
    }

    private sealed class SmokeSetupController : IFirstRunSetupController
    {
        private int _required = 1;

        public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout)
        {
            Interlocked.Exchange(ref _required, 0);
            return CreateResult();
        }

        public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null)
        {
            return CreateResult();
        }

        public FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout)
        {
            Interlocked.Exchange(ref _required, 0);
            return CreateResult();
        }

        public bool IsSetupComplete(DefaultStorageLayout layout) => Volatile.Read(ref _required) == 0;

        private FirstRunSetupResult CreateResult()
        {
            var required = Volatile.Read(ref _required) != 0;
            return new FirstRunSetupResult(
                Succeeded: !required,
                Code: required ? "setup_required" : "setup_complete",
                State: new FirstRunSetupState(
                    Required: required,
                    UnprotectedProfileIds: required ? new[] { "codex-desktop" } : Array.Empty<string>(),
                    Status: required ? "pending" : "complete",
                    VerifiedCodex: !required,
                    VerifiedChatGpt: !required),
                Diagnostics: new Dictionary<string, string>());
        }
    }
}
