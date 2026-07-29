using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal sealed record ResidentLifecycleSmokeReport(
    bool HookRegistrationPassed,
    bool SetupGatePassed,
    bool RuntimeReloadPassed,
    bool SelectedSendFailurePassed,
    bool SecondInstancePassed,
    bool NoCloudSubmissionPassed)
{
    public bool Passed => HookRegistrationPassed
        && SetupGatePassed
        && RuntimeReloadPassed
        && SelectedSendFailurePassed
        && SecondInstancePassed
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
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() => report = RunOnStaThread(directory, completed))
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            return thread.Join(Timeout) && completed.IsSet && report is not null
                ? report
                : Failed();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ResidentLifecycleSmokeReport RunOnStaThread(string directory, ManualResetEventSlim completed)
    {
        var hookRegistrationPassed = false;
        var setupGatePassed = false;
        var runtimeReloadPassed = false;
        var selectedSendFailurePassed = false;
        var secondInstancePassed = false;
        var cloudSubmissionCount = 0;
        var layout = DefaultStorageLayout.Create(directory);
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
                firstRunSetupController: setup,
                setupLayout: layout);
            var reloadedController = new NativeSubmitInterceptionController(
                protectedProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                firstRunSetupController: setup,
                setupLayout: layout);
            var protection = new TrayProtectionController(
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
                    new[] { new NativeSubmitRuntime(reloadedHook, reloadedController, NoCloudSubmission, protectedProfile) }),
                () => setup,
                _ =>
                {
                    runtimeReloadPassed = reloadedHook.IsKeyboardHookRegistered
                        && !initialHook.IsKeyboardHookRegistered
                        && protection.State.NativeSubmitStatus == OsInteractionStatusIds.Protected
                        && protection.State.ComposerProtected;
                    var selectedFailure = reloadedController.HandleIdentifiedSendControl(
                        TextSurfaceDiscoveryResult.Failure(
                            OsInteractionStatusIds.SurfaceUnverified,
                            new Dictionary<string, string> { ["profile_id"] = protectedProfile.ProfileId }));
                    selectedSendFailurePassed = selectedFailure.Status == OsInteractionStatusIds.SurfaceUnverified
                        && selectedFailure.SuppressOriginalInput
                        && !selectedFailure.Submitted;
                    using var second = new SingleInstanceEnforcement(instanceId);
                    secondInstancePassed = !second.IsFirstInstance;
                    completed.Set();
                });

            hookRegistrationPassed = initialHook.IsKeyboardHookRegistered;
            var setupGate = initialController.HandleGesture(new NativeKeyGesture("Enter"));
            setupGatePassed = protection.State.SetupRequired
                && protection.State.NativeSubmitStatus == OsInteractionStatusIds.NativeSubmitSetupRequired
                && setupGate.Status == OsInteractionStatusIds.NativeSubmitSetupRequired
                && setupGate.SuppressOriginalInput;

            using var timer = new System.Windows.Forms.Timer { Interval = 25 };
            var ticks = 0;
            timer.Tick += (_, _) =>
            {
                if (completed.IsSet || ++ticks > 240)
                {
                    timer.Stop();
                    context.ExitThread();
                }
            };
            timer.Start();
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
            selectedSendFailurePassed,
            secondInstancePassed,
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
        return new ResidentLifecycleSmokeReport(false, false, false, false, false, false);
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
