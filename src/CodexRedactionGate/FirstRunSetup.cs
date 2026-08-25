using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexRedactionGate;

public sealed record FirstRunSetupState(
    bool Required,
    IReadOnlyList<string> UnprotectedProfileIds,
    string Status,
    bool VerifiedCodex,
    bool VerifiedChatGpt);

public sealed record FirstRunSetupResult(
    bool Succeeded,
    string Code,
    FirstRunSetupState State,
    IReadOnlyDictionary<string, string> Diagnostics,
    IReadOnlyList<SubmitBindingProfile>? PreviousProfiles = null,
    IReadOnlyList<SubmitBindingProfile>? PendingProfiles = null,
    string? PreviousActiveTargetProfileId = null);

public interface IFirstRunSetupController
{
    FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout);
    FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null);
    FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout);
    bool IsSetupComplete(DefaultStorageLayout layout);
}

/// <summary>
/// Executes first-run setup without making the resident hook decide setup state
/// from an arbitrary protected profile. The caller owns scheduling; this class
/// deliberately has no WinForms dependency so the decision is regression-testable.
/// </summary>
internal sealed class FirstRunSetupLaunchCoordinator
{
    private readonly DefaultStorageLayout _layout;
    private readonly IFirstRunSetupController _setupController;

    public FirstRunSetupLaunchCoordinator(DefaultStorageLayout layout, IFirstRunSetupController setupController)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _setupController = setupController ?? throw new ArgumentNullException(nameof(setupController));
    }

    public FirstRunSetupResult RunIfRequired()
    {
        var status = _setupController.GetSetupStatus(_layout);
        if (!status.State.Required)
        {
            return status;
        }

        return _setupController.EnsureSetup(_layout);
    }
}

internal interface IFirstRunProfileVerifier
{
    SubmitBindingProfile Verify(SubmitBindingProfile profile);
}

internal sealed record FocusedProfileVerificationResult(
    SubmitBindingProfile? Profile,
    string Code,
    IReadOnlyDictionary<string, string> Diagnostics);

internal interface IFocusedFirstRunProfileVerifier
{
    FocusedProfileVerificationResult VerifyFocused(string submitBinding, string newlineBinding);
}

internal interface IObservableFocusedFirstRunProfileVerifier : IFocusedFirstRunProfileVerifier
{
    FocusedProfileVerificationResult VerifyFocused(
        string submitBinding,
        string newlineBinding,
        Action<PromptProtectionSetupProgress> publishProgress);
}

internal interface IFocusedProfileSetupController
{
    FirstRunSetupResult ConfigureFocusedProfile(DefaultStorageLayout layout);

    FirstRunSetupResult VerifyFocusedProfile(
        string submitBinding,
        string newlineBinding,
        DefaultStorageLayout layout);
}

internal interface ISetupVerificationProgressReporter
{
    long BeginSetupVerification(string binding, int remainingSeconds);

    void PublishSetupProgress(
        string status,
        string action,
        string? profileId = null,
        string binding = "not_configured",
        int remainingSeconds = 0,
        long attemptId = 0);
}

internal sealed class FocusedComposerFirstRunProfileVerifier : IFirstRunProfileVerifier, IObservableFocusedFirstRunProfileVerifier
{
    private readonly TimeSpan _verificationDelay;
    private readonly Func<TextSurfaceDiscoveryResult> _discoveryFactory;

    public FocusedComposerFirstRunProfileVerifier()
        : this(SetupVerificationCountdown.DefaultDelay, () => WindowsFocusedComposerDiscovery.CreateDefault().DiscoverActiveSurface())
    {
    }

    internal FocusedComposerFirstRunProfileVerifier(
        TimeSpan verificationDelay,
        Func<TextSurfaceDiscoveryResult> discoveryFactory)
    {
        _verificationDelay = verificationDelay;
        _discoveryFactory = discoveryFactory ?? throw new ArgumentNullException(nameof(discoveryFactory));
    }

    public SubmitBindingProfile Verify(SubmitBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_verificationDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_verificationDelay);
        }

        if (profile.SubmitBinding is null || profile.NewlineBinding is null)
        {
            return profile with
            {
                Enabled = true,
                BindingSource = "not_verified",
                CapabilityStatus = OsInteractionStatusIds.BindingUnknown
            };
        }

        var discovery = _discoveryFactory();
        return SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profile.ProfileId,
            profile.SubmitBinding.DisplayText,
            profile.NewlineBinding.DisplayText,
            discovery,
            profile.CompatibilityEvidence);
    }

    public FocusedProfileVerificationResult VerifyFocused(string submitBinding, string newlineBinding)
    {
        return VerifyFocused(submitBinding, newlineBinding, _ => { });
    }

    public FocusedProfileVerificationResult VerifyFocused(
        string submitBinding,
        string newlineBinding,
        Action<PromptProtectionSetupProgress> publishProgress)
    {
        ArgumentNullException.ThrowIfNull(publishProgress);
        if (_verificationDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_verificationDelay);
        }

        TextSurfaceDiscoveryResult discovery;
        try
        {
            discovery = _discoveryFactory();
        }
        catch (Exception exception)
        {
            return VerificationExceptionResult(exception, "surface_discovery");
        }
        var profileId = discovery.Surface?.ProfileId;
        if (CreateProfile(profileId) is not { } profile)
        {
            var discoveryDiagnostics = new Dictionary<string, string>(discovery.Diagnostics, StringComparer.Ordinal)
            {
                ["surface_status"] = discovery.Status
            };
            return new FocusedProfileVerificationResult(
                Profile: null,
                Code: "focused_surface_unverified",
                Diagnostics: discoveryDiagnostics);
        }

        publishProgress(new PromptProtectionSetupProgress(
            "composer_recognized", "wait_for_verification", profile.ProfileId, submitBinding));
        publishProgress(new PromptProtectionSetupProgress(
            "verifying_binding", "wait_for_verification", profile.ProfileId, submitBinding));

        SubmitBindingProfile verified;
        try
        {
            verified = SubmitBindingOnboardingVerifier.VerifyUserBindings(
                profile.ProfileId,
                submitBinding,
                newlineBinding,
                discovery,
                profile.CompatibilityEvidence);
        }
        catch (Exception exception)
        {
            return VerificationExceptionResult(exception, "binding_verification", profile.ProfileId);
        }
        var verificationDiagnostics = new Dictionary<string, string>(verified.Diagnostics, StringComparer.Ordinal)
        {
            ["profile_id"] = verified.ProfileId,
            ["verification_result"] = verified.CapabilityStatus
        };
        return new FocusedProfileVerificationResult(
            Profile: verified,
            Code: verified.IsProtected ? "focused_profile_verified" : "focused_profile_verification_failed",
            Diagnostics: verificationDiagnostics);
    }

    private static FocusedProfileVerificationResult VerificationExceptionResult(
        Exception exception,
        string stage,
        string? profileId = null)
    {
        LocalCrashDiagnostics.CaptureDefault(exception, "focused_profile_verification", stage);
        return new FocusedProfileVerificationResult(
            Profile: null,
            Code: "focused_profile_verification_failed",
            Diagnostics: new Dictionary<string, string>
            {
                ["verification_exception"] = "true",
                ["exception_type"] = exception.GetType().FullName ?? exception.GetType().Name,
                ["exception_stage"] = stage,
                ["profile_id"] = profileId ?? "not_detected"
            });
    }

    private static SubmitBindingProfile? CreateProfile(string? profileId)
    {
        return string.IsNullOrWhiteSpace(profileId)
            ? null
            : FirstRunSetupController.CreateDefaultSetupProfile(profileId);
    }
}

internal sealed class FirstRunSetupController : IFirstRunSetupController, IFocusedProfileSetupController, ISetupVerificationProgressReporter
{
    private static long _nextSetupAttemptId;
    private readonly IFirstRunProfileVerifier _profileVerifier;
    private readonly IFocusedFirstRunProfileVerifier _focusedProfileVerifier;
    private readonly Func<IReadOnlyList<SubmitBindingProfile>, DefaultStorageLayout, IFirstRunSetupController, bool> _showSetupWindow;
    private readonly Action<PromptProtectionSetupProgress>? _setupProgressPublisher;
    private readonly object _setupProgressGate = new();
    private long _setupAttemptId;
    private string _setupProgressStatus = "idle";
    private FirstRunSetupResult? _lastFocusedVerificationResult;

    public FirstRunSetupController()
        : this(new FocusedComposerFirstRunProfileVerifier(), ShowSetupWindow)
    {
    }

    internal FirstRunSetupController(Action<PromptProtectionSetupProgress> setupProgressPublisher)
        : this(new FocusedComposerFirstRunProfileVerifier(), ShowSetupWindow, null, setupProgressPublisher)
    {
    }

    internal FirstRunSetupController(
        IFirstRunProfileVerifier profileVerifier,
        Func<IReadOnlyList<SubmitBindingProfile>, DefaultStorageLayout, IFirstRunSetupController, bool>? showSetupWindow = null,
        IFocusedFirstRunProfileVerifier? focusedProfileVerifier = null,
        Action<PromptProtectionSetupProgress>? setupProgressPublisher = null)
    {
        _profileVerifier = profileVerifier ?? throw new ArgumentNullException(nameof(profileVerifier));
        _focusedProfileVerifier = focusedProfileVerifier
            ?? profileVerifier as IFocusedFirstRunProfileVerifier
            ?? new FocusedComposerFirstRunProfileVerifier();
        _showSetupWindow = showSetupWindow ?? ShowSetupWindow;
        _setupProgressPublisher = setupProgressPublisher;
    }

    public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var initialStatus = GetSetupStatus(layout);
        if (initialStatus.Succeeded && !initialStatus.State.Required)
        {
            return new FirstRunSetupResult(
                Succeeded: true,
                Code: "setup_complete",
                State: initialStatus.State,
                Diagnostics: initialStatus.Diagnostics);
        }

        // Do not launch a setup window that could overwrite an unreadable
        // profile store. The resident hook stays fail-closed until it recovers.
        if (string.Equals(initialStatus.Code, "profiles_load_failed", StringComparison.Ordinal))
        {
            return initialStatus;
        }

        if (!OperatingSystem.IsWindows())
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "setup_requires_windows",
                State: initialStatus.State,
                Diagnostics: Merge(initialStatus.Diagnostics, new Dictionary<string, string>
                {
                    ["platform"] = "non_windows"
                }));
        }

        return ConfigureFocusedProfile(layout);
    }

    public FirstRunSetupResult ConfigureFocusedProfile(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var initialStatus = GetSetupStatus(layout);
        if (string.Equals(initialStatus.Code, "profiles_load_failed", StringComparison.Ordinal))
        {
            return initialStatus;
        }

        if (!OperatingSystem.IsWindows())
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "setup_requires_windows",
                State: initialStatus.State,
                Diagnostics: Merge(initialStatus.Diagnostics, new Dictionary<string, string>
                {
                    ["platform"] = "non_windows"
                }));
        }

        var storeResult = SubmitBindingProfileStore.Load(layout);
        var setupCompleted = _showSetupWindow(storeResult.Profiles, layout, this);
        if (setupCompleted)
        {
            if (_lastFocusedVerificationResult is { Succeeded: true, PendingProfiles: not null } pendingResult
                && !pendingResult.State.Required)
            {
                return pendingResult with
                {
                    Diagnostics = Merge(pendingResult.Diagnostics, new Dictionary<string, string>
                    {
                        ["user_action"] = "setup_window_closed",
                        ["all_profiles_verified"] = "true"
                    })
                };
            }

            var finalStatus = GetSetupStatus(layout);
            if (finalStatus.Succeeded && !finalStatus.State.Required)
            {
                return new FirstRunSetupResult(
                    Succeeded: true,
                    Code: "setup_complete_after_window",
                    State: finalStatus.State,
                    Diagnostics: Merge(_lastFocusedVerificationResult?.Diagnostics ?? finalStatus.Diagnostics, new Dictionary<string, string>
                    {
                        ["user_action"] = "setup_window_closed",
                        ["all_profiles_verified"] = "true"
                    }),
                    PreviousProfiles: _lastFocusedVerificationResult?.PreviousProfiles,
                    PendingProfiles: _lastFocusedVerificationResult?.PendingProfiles);
            }

            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "setup_incomplete_unprotected_profiles",
                State: finalStatus.State,
                Diagnostics: Merge(finalStatus.Diagnostics, new Dictionary<string, string>
                {
                    ["user_action"] = "setup_window_closed",
                    ["all_profiles_verified"] = "false"
                }));
        }

        return new FirstRunSetupResult(
            Succeeded: false,
            Code: "setup_cancelled",
            State: initialStatus.State,
            Diagnostics: Merge(initialStatus.Diagnostics, new Dictionary<string, string>
            {
                ["user_action"] = "setup_window_cancelled"
            }));
    }

    public FirstRunSetupResult GetSetupStatus(DefaultStorageLayout layout, string? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var storeResult = SubmitBindingProfileStore.Load(layout);
        if (!storeResult.Succeeded)
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "profiles_load_failed",
                State: CreateSetupState(storeResult.Profiles),
                Diagnostics: new Dictionary<string, string>
                {
                    ["profiles_load_status"] = storeResult.Code
                });
        }

        var target = profileId is null
            ? ActivePromptProtectionTargetStore.Load(layout)
            : new ActivePromptProtectionTargetStoreResult(true, "target_explicit", profileId);
        var state = target.Succeeded && target.ProfileId is not null
            ? CreateSetupState(storeResult.Profiles, target.ProfileId)
            : CreateSetupStateForMissingTarget();
        return new FirstRunSetupResult(
            Succeeded: !state.Required,
            Code: state.Required ? "setup_required" : "setup_complete",
            State: state,
            Diagnostics: new Dictionary<string, string>
            {
                ["profiles_load_status"] = storeResult.Code,
                ["profile_count"] = storeResult.Profiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unprotected_profile_count"] = state.UnprotectedProfileIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["active_target_status"] = target.Code,
                ["active_target_profile_id"] = target.ProfileId ?? "not_configured"
            });
    }

    public FirstRunSetupResult VerifyProfile(string profileId, DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(layout);

        var storeResult = SubmitBindingProfileStore.Load(layout);
        if (!storeResult.Succeeded)
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "profiles_load_failed",
                State: CreateSetupState(storeResult.Profiles, profileId),
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = profileId,
                    ["profiles_load_status"] = storeResult.Code
                });
        }

        var profile = storeResult.Profiles
            .FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.Ordinal));

        profile ??= CreateDefaultSetupProfile(profileId);

        if (profile is null)
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "profile_not_found",
                State: CreateSetupState(storeResult.Profiles),
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = profileId
                });
        }

        SubmitBindingProfile verifiedProfile;
        try
        {
            verifiedProfile = _profileVerifier.Verify(profile);
        }
        catch (Exception)
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "verification_failed",
                State: CreateSetupState(storeResult.Profiles, profileId),
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = profileId,
                    ["verification_exception"] = "true"
                });
        }
        var saveResult = SubmitBindingProfileStore.Upsert(layout, verifiedProfile);
        if (!saveResult.Succeeded)
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "profile_update_failed",
                State: CreateSetupState(storeResult.Profiles, profileId),
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = profileId,
                    ["save_status"] = saveResult.Code
                });
        }

        var updatedStatus = GetSetupStatus(layout);

        return new FirstRunSetupResult(
            Succeeded: verifiedProfile.IsProtected,
            Code: verifiedProfile.IsProtected ? "profile_verified" : "verification_failed",
            State: updatedStatus.State,
            Diagnostics: Merge(updatedStatus.Diagnostics, new Dictionary<string, string>
            {
                ["profile_id"] = profileId,
                ["verification_result"] = verifiedProfile.CapabilityStatus,
                ["binding_source"] = verifiedProfile.BindingSource
            }));
    }

    public FirstRunSetupResult VerifyFocusedProfile(
        string submitBinding,
        string newlineBinding,
        DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        FocusedProfileVerificationResult focusedResult;
        try
        {
            focusedResult = _focusedProfileVerifier is IObservableFocusedFirstRunProfileVerifier observableVerifier
                ? observableVerifier.VerifyFocused(
                    submitBinding,
                    newlineBinding,
                    progress => PublishSetupProgress(
                        progress.Status,
                        progress.Action,
                        progress.ProfileId,
                        progress.Binding,
                        progress.RemainingSeconds))
                : VerifyFocusedWithoutProgress(submitBinding, newlineBinding);
        }
        catch (Exception exception)
        {
            LocalCrashDiagnostics.CaptureDefault(
                exception,
                "focused_profile_verification",
                "verification_failed");
            PublishSetupProgress("verification_failed", "retry_setup", binding: submitBinding);
            var currentStatus = GetSetupStatus(layout);
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "focused_profile_verification_failed",
                State: currentStatus.State,
                Diagnostics: new Dictionary<string, string>
                {
                    ["verification_exception"] = "true",
                    ["exception_type"] = exception.GetType().FullName ?? exception.GetType().Name,
                    ["exception_stage"] = "focused_verifier_or_progress"
                });
        }

        if (focusedResult.Profile is not { } profile || !profile.IsProtected)
        {
            PublishSetupProgress(
                focusedResult.Code == "focused_surface_unverified" ? "unsupported_surface" : "verification_failed",
                "retry_setup",
                binding: submitBinding);
            var currentStatus = GetSetupStatus(layout);
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: focusedResult.Code,
                State: currentStatus.State,
                Diagnostics: focusedResult.Diagnostics);
        }

        var previousProfiles = SubmitBindingProfileStore.Load(layout);
        if (!previousProfiles.Succeeded)
        {
            PublishSetupProgress("verification_failed", "retry_setup", profile.ProfileId, submitBinding);
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "focused_profile_update_failed",
                State: GetSetupStatus(layout).State,
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = profile.ProfileId,
                    ["save_status"] = previousProfiles.Code
                });
        }

        var previousTarget = ActivePromptProtectionTargetStore.Load(layout);
        if (!previousTarget.Succeeded)
        {
            PublishSetupProgress("verification_failed", "retry_setup", profile.ProfileId, submitBinding);
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "focused_target_load_failed",
                State: GetSetupStatus(layout).State,
                Diagnostics: new Dictionary<string, string>
                {
                    ["active_target_status"] = previousTarget.Code
                });
        }

        var pendingProfiles = previousProfiles.Profiles
            .Where(item => !string.Equals(item.ProfileId, profile.ProfileId, StringComparison.Ordinal))
            .Append(profile)
            .OrderBy(item => item.ProfileId, StringComparer.Ordinal)
            .ToArray();
        var pendingState = CreateSetupState(pendingProfiles, profile.ProfileId);
        PublishSetupProgress("activating_protection", "wait_for_verification", profile.ProfileId, submitBinding);
        var verifiedResult = new FirstRunSetupResult(
            Succeeded: true,
            Code: "focused_profile_verified",
            State: pendingState,
            Diagnostics: Merge(focusedResult.Diagnostics, new Dictionary<string, string>
            {
                ["profile_id"] = profile.ProfileId,
                ["verification_result"] = profile.CapabilityStatus,
                ["binding_source"] = profile.BindingSource,
                ["setup_attempt_id"] = _setupAttemptId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }),
            PreviousProfiles: previousProfiles.Profiles,
            PendingProfiles: pendingProfiles,
            PreviousActiveTargetProfileId: previousTarget.ProfileId);
        _lastFocusedVerificationResult = verifiedResult;
        return verifiedResult;
    }

    private FocusedProfileVerificationResult VerifyFocusedWithoutProgress(string submitBinding, string newlineBinding)
    {
        PublishSetupProgress("verifying_binding", "wait_for_verification", binding: submitBinding);
        return _focusedProfileVerifier.VerifyFocused(submitBinding, newlineBinding);
    }

    public void PublishSetupProgress(
        string status,
        string action,
        string? profileId = null,
        string binding = "not_configured",
        int remainingSeconds = 0,
        long attemptId = 0)
    {
        lock (_setupProgressGate)
        {
            if (status == "waiting_for_focus")
            {
                if (_setupAttemptId == 0)
                {
                    _setupAttemptId = Interlocked.Increment(ref _nextSetupAttemptId);
                    _setupProgressStatus = "waiting_for_focus";
                }
                else if ((attemptId != 0 && attemptId != _setupAttemptId)
                    || _setupProgressStatus != "waiting_for_focus")
                {
                    return;
                }

                _setupProgressPublisher?.Invoke(new PromptProtectionSetupProgress(
                    status,
                    action,
                    profileId,
                    binding,
                    _setupAttemptId,
                    Math.Max(remainingSeconds, 0)));
                return;
            }

            if (_setupAttemptId == 0)
            {
                _setupAttemptId = Interlocked.Increment(ref _nextSetupAttemptId);
                _setupProgressStatus = "waiting_for_focus";
                _setupProgressPublisher?.Invoke(new PromptProtectionSetupProgress(
                    "waiting_for_focus", "focus_message_composer", profileId, binding, _setupAttemptId));
            }
            else if (attemptId != 0 && attemptId != _setupAttemptId)
            {
                return;
            }

            _setupProgressStatus = status;
            _setupProgressPublisher?.Invoke(new PromptProtectionSetupProgress(
                status,
                action,
                profileId,
                binding,
                _setupAttemptId,
                Math.Max(remainingSeconds, 0)));
        }
    }

    public long BeginSetupVerification(string binding, int remainingSeconds)
    {
        lock (_setupProgressGate)
        {
            _setupAttemptId = Interlocked.Increment(ref _nextSetupAttemptId);
            _setupProgressStatus = "waiting_for_focus";
            _setupProgressPublisher?.Invoke(new PromptProtectionSetupProgress(
                "waiting_for_focus",
                "focus_message_composer",
                Binding: binding,
                AttemptId: _setupAttemptId,
                RemainingSeconds: Math.Max(remainingSeconds, 0)));
            return _setupAttemptId;
        }
    }

    public bool IsSetupComplete(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var status = GetSetupStatus(layout);
        return status.Succeeded && !status.State.Required;
    }

    internal static bool MarkSetupComplete(DefaultStorageLayout layout, string profileId)
    {
        if (!ActivePromptProtectionTargetStore.Save(layout, profileId).Succeeded)
        {
            return false;
        }

        AtomicFileWriter.WriteAllBytes(
            Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete"),
            System.Text.Encoding.UTF8.GetBytes("complete"));
        return true;
    }

    internal static SubmitBindingProfile? CreateDefaultSetupProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        return profileId switch
        {
            "codex-desktop" or "chatgpt-desktop" => new SubmitBindingProfile(
                profileId,
                Enabled: true,
                BindingSource: "not_verified",
                SubmitBinding: null,
                NewlineBinding: null,
                CapabilityStatus: OsInteractionStatusIds.BindingUnknown,
                CompatibilityEvidence: null,
                Diagnostics: new Dictionary<string, string>
                {
                    ["cloud_submission"] = "false",
                    ["setup_default_profile"] = "true"
                }),
            _ => null
        };
    }

    internal static IReadOnlyList<SubmitBindingProfile> SetupVisibleProfiles(IReadOnlyList<SubmitBindingProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var requiredDefaults = new[] { "codex-desktop", "chatgpt-desktop" }
            .Where(profileId => !profiles.Any(profile => string.Equals(profile.ProfileId, profileId, StringComparison.Ordinal)))
            .Select(profileId => CreateDefaultSetupProfile(profileId)!);
        return profiles.Concat(requiredDefaults).ToArray();
    }

    private static FirstRunSetupState CreateSetupState(IReadOnlyList<SubmitBindingProfile> profiles)
    {
        var protectedProfiles = profiles.Where(p => p.IsSetupComplete).ToArray();
        var codexProfile = profiles.FirstOrDefault(p => string.Equals(p.ProfileId, "codex-desktop", StringComparison.Ordinal));
        var chatGptProfile = profiles.FirstOrDefault(p => string.Equals(p.ProfileId, "chatgpt-desktop", StringComparison.Ordinal));

        return new FirstRunSetupState(
            Required: protectedProfiles.Length == 0,
            UnprotectedProfileIds: protectedProfiles.Length == 0 ? new[] { "focused_supported_app" } : Array.Empty<string>(),
            Status: protectedProfiles.Length == 0 ? "pending" : "complete",
            VerifiedCodex: codexProfile?.IsSetupComplete ?? false,
            VerifiedChatGpt: chatGptProfile?.IsSetupComplete ?? false);
    }

    private static FirstRunSetupState CreateSetupStateForMissingTarget()
    {
        return new FirstRunSetupState(
            Required: true,
            UnprotectedProfileIds: new[] { "focused_supported_app" },
            Status: "pending",
            VerifiedCodex: false,
            VerifiedChatGpt: false);
    }

    private static FirstRunSetupState CreateSetupState(IReadOnlyList<SubmitBindingProfile> profiles, string profileId)
    {
        var visibleProfiles = SetupVisibleProfiles(profiles);
        var selectedProfile = visibleProfiles.FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.Ordinal));
        var setupComplete = selectedProfile?.IsSetupComplete ?? false;

        return new FirstRunSetupState(
            Required: !setupComplete,
            UnprotectedProfileIds: setupComplete ? Array.Empty<string>() : new[] { profileId },
            Status: setupComplete ? "complete" : "pending",
            VerifiedCodex: profileId == "codex-desktop" && setupComplete,
            VerifiedChatGpt: profileId == "chatgpt-desktop" && setupComplete);
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var item in second)
        {
            merged[item.Key] = item.Value;
        }

        return merged;
    }

    private static bool ShowSetupWindow(
        IReadOnlyList<SubmitBindingProfile> profiles,
        DefaultStorageLayout layout,
        IFirstRunSetupController setupController)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var result = false;
        var thread = new Thread(() =>
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            using var form = new FirstRunSetupForm(layout, setupController);
            Application.Run(form);
            result = form.SetupCompleted;
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return result;
    }
}

internal sealed class FirstRunSetupForm : Form
{
    private readonly DefaultStorageLayout _layout;
    private readonly IFirstRunSetupController _setupController;
    private bool _setupCompleted;
    private Button? _verifyFocusedAppButton;
    private Button? _skipButton;
    private RadioButton? _enterSendRadioButton;
    private RadioButton? _ctrlEnterSendRadioButton;
    private Label? _bindingPairLabel;
    private Label? _verificationStatusLabel;
    private ProgressBar? _verificationProgressBar;
    private System.Windows.Forms.Timer? _verificationCountdownTimer;
    private SetupVerificationCountdown? _verificationCountdown;
    private long _verificationAttemptId;
    private long _verificationGeneration;

    public bool SetupCompleted => _setupCompleted;

    internal string VerificationStatusText => _verificationStatusLabel?.Text ?? string.Empty;

    internal int VerificationProgressMaximum => _verificationProgressBar?.Maximum ?? 0;

    internal int VerificationProgressValue => _verificationProgressBar?.Value ?? 0;

    public FirstRunSetupForm(
        DefaultStorageLayout layout,
        IFirstRunSetupController setupController)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _setupController = setupController ?? throw new ArgumentNullException(nameof(setupController));

        Text = "First-Time Setup - Codex Redaction Gate";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 760;
        Height = 430;
        MinimizeBox = false;
        MaximizeBox = false;
        TopMost = true;

        InitializeComponents();
        FormClosed += (_, _) => StopVerificationCountdown();
    }

    private void InitializeComponents()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 5
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var instructionLabel = new Label
        {
            Text = "Step 1 of 3: choose the Send key used by OpenAI Desktop.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 8)
        };

        var instructionLabel2 = new Label
        {
            Text = "Step 2 of 3: click Verify active app, then click inside its message composer before the countdown reaches zero.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 12)
        };

        // Binding pair selection
        var bindingSelectionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 12)
        };

        var bindingLabel = new Label
        {
            Text = "Send key binding:",
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 0)
        };

        _enterSendRadioButton = new RadioButton
        {
            Text = "Enter as Send / Ctrl+Enter as newline",
            AutoSize = true,
            Margin = new Padding(0, 6, 12, 0),
            Checked = false
        };

        _ctrlEnterSendRadioButton = new RadioButton
        {
            Text = "Ctrl+Enter as Send / Enter as newline",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0)
        };

        _bindingPairLabel = new Label
        {
            Text = "Select the application's Send key before verification.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 8),
            Font = new Font(Font.FontFamily, 9f, FontStyle.Italic),
            ForeColor = Color.DarkBlue
        };

        // Update binding pair label when radio buttons change
        _enterSendRadioButton.CheckedChanged += (_, _) => UpdateBindingPairLabel();
        _ctrlEnterSendRadioButton.CheckedChanged += (_, _) => UpdateBindingPairLabel();

        bindingSelectionPanel.Controls.Add(bindingLabel);
        bindingSelectionPanel.Controls.Add(_enterSendRadioButton);
        bindingSelectionPanel.Controls.Add(_ctrlEnterSendRadioButton);
        var verificationPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0, 10, 0, 10)
        };
        verificationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        verificationPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        verificationPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        verificationPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _verificationStatusLabel = new Label
        {
            Text = "Stage: waiting to start. Result: not verified. Protected Send remains blocked.",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 8),
            ForeColor = Color.DarkBlue
        };

        _verificationProgressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Minimum = 0,
            Maximum = SetupVerificationCountdown.DefaultDelay.Seconds,
            Value = 0,
            Height = 18
        };
        verificationPanel.Controls.Add(_bindingPairLabel, 0, 0);
        verificationPanel.Controls.Add(_verificationProgressBar, 0, 1);
        verificationPanel.Controls.Add(_verificationStatusLabel, 0, 2);

        var buttonsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0)
        };

        _verifyFocusedAppButton = new Button
        {
            Text = "Verify active app",
            Width = 160,
            Margin = new Padding(0, 0, 8, 0)
        };
        _verifyFocusedAppButton.Click += (_, _) => OnVerifyFocusedProfile();

        _skipButton = new Button
        {
            Text = "Exit setup",
            Width = 140
        };
        _skipButton.Click += (_, _) => OnSkipSetup();

        buttonsPanel.Controls.Add(_verifyFocusedAppButton);
        buttonsPanel.Controls.Add(_skipButton);

        root.Controls.Add(instructionLabel, 0, 0);
        root.Controls.Add(instructionLabel2, 0, 1);
        root.Controls.Add(bindingSelectionPanel, 0, 2);
        root.Controls.Add(verificationPanel, 0, 3);
        root.Controls.Add(buttonsPanel, 0, 4);
        Controls.Add(root);

        AcceptButton = _verifyFocusedAppButton;
        CancelButton = _skipButton;
    }

    private void UpdateBindingPairLabel()
    {
        if (_enterSendRadioButton is null || _ctrlEnterSendRadioButton is null || _bindingPairLabel is null)
        {
            return;
        }

        if (_enterSendRadioButton.Checked)
        {
            _bindingPairLabel.Text = "Currently selected: Enter Send / Ctrl+Enter Newline";
        }
        else if (_ctrlEnterSendRadioButton.Checked)
        {
            _bindingPairLabel.Text = "Currently selected: Ctrl+Enter Send / Enter Newline";
        }
    }

    private (string SubmitBinding, string NewlineBinding) GetSelectedBindingPair()
    {
        if (_enterSendRadioButton?.Checked == true)
        {
            return ("Enter", "Ctrl+Enter");
        }
        else if (_ctrlEnterSendRadioButton?.Checked == true)
        {
            return ("Ctrl+Enter", "Enter");
        }
        return (string.Empty, string.Empty);
    }

    private async void OnVerifyFocusedProfile()
    {
        if (_setupCompleted)
        {
            Close();
            return;
        }

        var (selectedSubmit, selectedNewline) = GetSelectedBindingPair();
        if (string.IsNullOrEmpty(selectedSubmit) || string.IsNullOrEmpty(selectedNewline))
        {
            MessageBox.Show(
                "Select the application's Send key before verification.",
                "Codex Redaction Gate - Setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_setupController is not IFocusedProfileSetupController focusedSetupController)
        {
            MessageBox.Show(
                "This installation cannot verify the active application. Protected Send remains blocked.",
                "Codex Redaction Gate - Setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var verificationGeneration = Interlocked.Increment(ref _verificationGeneration);
        StartVerificationCountdown(selectedSubmit);
        _verificationStatusLabel!.ForeColor = Color.DarkOrange;
        _verifyFocusedAppButton!.Enabled = false;
        _verifyFocusedAppButton.Text = "Verification running...";
        TopMost = false;
        var result = await FocusedProfileVerificationWorker.RunAsync(
            () => focusedSetupController.VerifyFocusedProfile(selectedSubmit, selectedNewline, _layout));

        if (IsDisposed || verificationGeneration != Volatile.Read(ref _verificationGeneration))
        {
            return;
        }

        StopVerificationCountdown();
        _verifyFocusedAppButton.Enabled = true;
        _verifyFocusedAppButton.Text = "Verify active app";
        TopMost = true;
        Activate();

        if (!result.Succeeded)
        {
            var reason = VerificationFailureReason(result.Diagnostics);
            _verificationStatusLabel.Text = $"Stage: verification complete.{Environment.NewLine}Result: failed ({reason}).{Environment.NewLine}Next: keep this window open, correct the focus, then click Verify active app again. Protected Send remains blocked.";
            _verificationStatusLabel.ForeColor = Color.DarkRed;
            return;
        }

        var profileId = result.Diagnostics.TryGetValue("profile_id", out var verifiedProfileId)
            ? PromptProtectionSetupLifecycle.SafeProfileId(verifiedProfileId)
            : "selected_desktop_app";
        var profileName = profileId switch
        {
            "codex-desktop" => "Codex Desktop",
            "chatgpt-desktop" => "ChatGPT Desktop",
            _ => "selected desktop app"
        };
        _verificationStatusLabel.Text = $"Stage: verification complete.{Environment.NewLine}Result: {profileName} verified successfully.{Environment.NewLine}Next: click Finish and activate protection.";
        _verificationStatusLabel.ForeColor = Color.DarkGreen;
        _setupCompleted = true;
        _verifyFocusedAppButton.Text = "Finish and activate";
        _skipButton!.Enabled = false;
    }

    private void StartVerificationCountdown(string submitBinding)
    {
        StopVerificationCountdown();
        _verificationCountdown = new SetupVerificationCountdown(SetupVerificationCountdown.DefaultDelay);
        var remainingSeconds = _verificationCountdown.Start();
        UpdateVerificationCountdownText(remainingSeconds);
        _verificationAttemptId = _setupController is ISetupVerificationProgressReporter setupProgressReporter
            ? setupProgressReporter.BeginSetupVerification(submitBinding, remainingSeconds)
            : 0;
        _verificationCountdownTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _verificationCountdownTimer.Tick += (_, _) =>
        {
            if (_verificationCountdown is not null)
            {
                var remaining = _verificationCountdown.Tick();
                UpdateVerificationCountdownText(remaining);
                PublishWaitingForFocusProgress(submitBinding, remaining, _verificationAttemptId);
            }
        };
        _verificationCountdownTimer.Start();
    }

    internal void StartVerificationCountdownForAcceptance(string submitBinding) =>
        StartVerificationCountdown(submitBinding);

    private void PublishWaitingForFocusProgress(string submitBinding, int remainingSeconds, long attemptId)
    {
        if (_setupController is ISetupVerificationProgressReporter setupProgressReporter)
        {
            setupProgressReporter.PublishSetupProgress(
                "waiting_for_focus",
                "focus_message_composer",
                binding: submitBinding,
                remainingSeconds: remainingSeconds,
                attemptId: attemptId);
        }
    }

    private void StopVerificationCountdown()
    {
        if (_verificationCountdownTimer is not null)
        {
            _verificationCountdownTimer.Stop();
            _verificationCountdownTimer.Dispose();
            _verificationCountdownTimer = null;
        }

        _verificationCountdown = null;
        _verificationAttemptId = 0;
    }

    private void UpdateVerificationCountdownText(int remainingSeconds)
    {
        if (_verificationStatusLabel is null)
        {
            return;
        }

        var unit = remainingSeconds == 1 ? "second" : "seconds";
        if (_verificationProgressBar is not null && _verificationCountdown is not null)
        {
            _verificationProgressBar.Maximum = Math.Max(_verificationCountdown.TotalSeconds, 1);
            _verificationProgressBar.Value = Math.Clamp(
                _verificationCountdown.TotalSeconds - remainingSeconds,
                _verificationProgressBar.Minimum,
                _verificationProgressBar.Maximum);
        }
        _verificationStatusLabel.Text = remainingSeconds > 0
            ? $"Stage: waiting for focus ({remainingSeconds} {unit} remaining).{Environment.NewLine}Action: click inside the OpenAI Desktop message composer now.{Environment.NewLine}Result: pending; Protected Send remains blocked."
            : $"Stage: reading focused composer.{Environment.NewLine}Action: wait for the local result.{Environment.NewLine}Result: pending; Protected Send remains blocked.";
    }

    internal static string BuildVerificationFailureMessage(FirstRunSetupResult result)
    {
        var reason = VerificationFailureReason(result.Diagnostics);
        var profileId = DiagnosticValue(result.Diagnostics, "profile_id");
        var verificationResult = DiagnosticValue(result.Diagnostics, "verification_result");
        var compatibility = DiagnosticValue(result.Diagnostics, "compatibility");
        var profileMatches = DiagnosticValue(result.Diagnostics, "profile_match_count");
        var controlType = DiagnosticValue(result.Diagnostics, "element_control_type");
        var framework = DiagnosticValue(result.Diagnostics, "element_framework_id");
        var hasFocus = DiagnosticValue(result.Diagnostics, "has_keyboard_focus");
        var textPattern = DiagnosticValue(result.Diagnostics, "can_read_text_pattern");
        var applicationVersion = DiagnosticValue(result.Diagnostics, "application_version_status");
        var focusResolution = DiagnosticValue(result.Diagnostics, "focus_resolution_stage");
        var exceptionType = DiagnosticValue(result.Diagnostics, "exception_type");
        var exceptionStage = DiagnosticValue(result.Diagnostics, "exception_stage");

        return $"Verification did not confirm the focused composer.\n\n"
            + $"Reason: {reason}\n"
            + $"Exception: {exceptionType}\n"
            + $"Stage: {exceptionStage}\n"
            + $"Profile: {profileId}\n"
            + $"Verification result: {verificationResult}\n"
            + $"Compatibility: {compatibility}\n"
            + $"Matching OpenAI profiles: {profileMatches}\n"
            + $"Focused control: {controlType}\n"
            + $"Framework: {framework}\n"
            + $"Keyboard focus: {hasFocus}\n"
            + $"Text access: {textPattern}\n"
            + $"Application version access: {applicationVersion}\n"
            + $"Focus resolution: {focusResolution}\n\n"
            + "Click inside the message composer in Codex or ChatGPT Desktop and try again. "
            + "Protected Send remains blocked until setup succeeds.";
    }

    private static string VerificationFailureReason(IReadOnlyDictionary<string, string> diagnostics)
    {
        if (diagnostics.TryGetValue("surface_status", out var surfaceStatus)
            && !string.IsNullOrWhiteSpace(surfaceStatus))
        {
            return surfaceStatus;
        }

        if (diagnostics.TryGetValue("classification_reason", out var classificationReason)
            && !string.IsNullOrWhiteSpace(classificationReason))
        {
            return classificationReason;
        }

        if (diagnostics.TryGetValue("compatibility", out var compatibility)
            && !string.IsNullOrWhiteSpace(compatibility))
        {
            return compatibility;
        }

        if (diagnostics.TryGetValue("binding_error", out var bindingError)
            && !string.IsNullOrWhiteSpace(bindingError))
        {
            return bindingError;
        }

        if (diagnostics.TryGetValue("verification_result", out var verificationResult)
            && !string.IsNullOrWhiteSpace(verificationResult))
        {
            return verificationResult;
        }

        return "verification_failed";
    }

    private static string DiagnosticValue(
        IReadOnlyDictionary<string, string> diagnostics,
        string key)
    {
        return diagnostics.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : diagnostics.TryGetValue($"surface.{key}", out var surfaceValue) && !string.IsNullOrWhiteSpace(surfaceValue)
                ? surfaceValue
                : "unknown";
    }

    private void OnSkipSetup()
    {
        Interlocked.Increment(ref _verificationGeneration);
        var confirmed = MessageBox.Show(
            "Exit setup? Codex/ChatGPT protected Send will remain blocked until profile verification succeeds.",
            "Code Sanitizer - setup is not complete",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        if (!confirmed)
        {
            return;
        }

        _setupCompleted = false;
        StopVerificationCountdown();
        Close();
    }
}

internal static class FocusedProfileVerificationWorker
{
    internal static Task<T> RunAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "CodexRedactionGate.ProfileVerification"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
