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
    IReadOnlyList<SubmitBindingProfile>? PendingProfiles = null);

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
    void PublishSetupProgress(string status, string action, string? profileId = null, string binding = "not_configured");
}

internal sealed class FocusedComposerFirstRunProfileVerifier : IFirstRunProfileVerifier, IObservableFocusedFirstRunProfileVerifier
{
    private readonly TimeSpan _verificationDelay;
    private readonly Func<TextSurfaceDiscoveryResult> _discoveryFactory;

    public FocusedComposerFirstRunProfileVerifier()
        : this(TimeSpan.FromSeconds(10), () => WindowsFocusedComposerDiscovery.CreateDefault().DiscoverActiveSurface())
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

        var discovery = _discoveryFactory();
        var profileId = discovery.Surface?.ProfileId;
        if (CreateProfile(profileId) is not { } profile)
        {
            return new FocusedProfileVerificationResult(
                Profile: null,
                Code: "focused_surface_unverified",
                Diagnostics: new Dictionary<string, string>
                {
                    ["surface_status"] = discovery.Status
                });
        }

        publishProgress(new PromptProtectionSetupProgress(
            "composer_recognized", "wait_for_verification", profile.ProfileId, submitBinding));
        publishProgress(new PromptProtectionSetupProgress(
            "verifying_binding", "wait_for_verification", profile.ProfileId, submitBinding));

        var verified = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profile.ProfileId,
            submitBinding,
            newlineBinding,
            discovery,
            profile.CompatibilityEvidence);
        return new FocusedProfileVerificationResult(
            Profile: verified,
            Code: verified.IsProtected ? "focused_profile_verified" : "focused_profile_verification_failed",
            Diagnostics: verified.Diagnostics);
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
    private long _setupAttemptId;
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

        var state = profileId is null
            ? CreateSetupState(storeResult.Profiles)
            : CreateSetupState(storeResult.Profiles, profileId);
        return new FirstRunSetupResult(
            Succeeded: !state.Required,
            Code: state.Required ? "setup_required" : "setup_complete",
            State: state,
            Diagnostics: new Dictionary<string, string>
            {
                ["profiles_load_status"] = storeResult.Code,
                ["profile_count"] = storeResult.Profiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["unprotected_profile_count"] = state.UnprotectedProfileIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
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
                        progress.Binding))
                : VerifyFocusedWithoutProgress(submitBinding, newlineBinding);
        }
        catch (Exception)
        {
            PublishSetupProgress("verification_failed", "retry_setup", binding: submitBinding);
            var currentStatus = GetSetupStatus(layout);
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "focused_profile_verification_failed",
                State: currentStatus.State,
                Diagnostics: new Dictionary<string, string>
                {
                    ["verification_exception"] = "true"
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
            PendingProfiles: pendingProfiles);
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
        string binding = "not_configured")
    {
        if (status == "waiting_for_focus")
        {
            _setupAttemptId = Interlocked.Increment(ref _nextSetupAttemptId);
        }
        else if (_setupAttemptId == 0)
        {
            _setupAttemptId = Interlocked.Increment(ref _nextSetupAttemptId);
            _setupProgressPublisher?.Invoke(new PromptProtectionSetupProgress(
                "waiting_for_focus", "focus_message_composer", profileId, binding, _setupAttemptId));
        }

        _setupProgressPublisher?.Invoke(new PromptProtectionSetupProgress(status, action, profileId, binding, _setupAttemptId));
    }

    public bool IsSetupComplete(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var setupMarkerPath = Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete");
        return File.Exists(setupMarkerPath);
    }

    internal static void MarkSetupComplete(DefaultStorageLayout layout)
    {
        SetSetupComplete(layout);
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

    private static void SetSetupComplete(DefaultStorageLayout layout)
    {
        layout.EnsureDirectories();
        var setupMarkerPath = Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete");
        AtomicFileWriter.WriteAllBytes(
            setupMarkerPath,
            System.Text.Encoding.UTF8.GetBytes($"complete:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"));
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
    private long _verificationGeneration;

    public bool SetupCompleted => _setupCompleted;

    public FirstRunSetupForm(
        DefaultStorageLayout layout,
        IFirstRunSetupController setupController)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _setupController = setupController ?? throw new ArgumentNullException(nameof(setupController));

        Text = "First-Time Setup - Codex Redaction Gate";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 700;
        Height = 330;
        MinimizeBox = false;
        MaximizeBox = false;
        TopMost = true;

        InitializeComponents();
    }

    private void InitializeComponents()
    {
        var instructionLabel = new Label
        {
            Text = "Before you can use protected send, verify the Codex or ChatGPT Desktop window you use now.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 60,
            Padding = new Padding(12, 12, 12, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var instructionLabel2 = new Label
        {
            Text = "Choose the Send key, click Verify active app, then focus its message composer. The app type is detected locally.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 45,
            Padding = new Padding(12, 0, 12, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        // Binding pair selection
        var bindingSelectionPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(12, 0, 12, 12),
            BackColor = SystemColors.Control
        };

        var bindingLabel = new Label
        {
            Text = "Send key binding:",
            Dock = DockStyle.Left,
            AutoSize = true,
            Padding = new Padding(0, 10, 10, 0)
        };

        _enterSendRadioButton = new RadioButton
        {
            Text = "Enter as Send / Ctrl+Enter as newline",
            Dock = DockStyle.Left,
            AutoSize = true,
            Margin = new Padding(0, 8, 10, 0),
            Checked = false
        };

        _ctrlEnterSendRadioButton = new RadioButton
        {
            Text = "Ctrl+Enter as Send / Enter as newline",
            Dock = DockStyle.Left,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };

        _bindingPairLabel = new Label
        {
            Text = "Select the application's Send key before verification.",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12, 5, 12, 0),
            Font = new Font(Font.FontFamily, 9f, FontStyle.Italic),
            ForeColor = Color.DarkBlue
        };

        // Update binding pair label when radio buttons change
        _enterSendRadioButton.CheckedChanged += (_, _) => UpdateBindingPairLabel();
        _ctrlEnterSendRadioButton.CheckedChanged += (_, _) => UpdateBindingPairLabel();

        bindingSelectionPanel.Controls.Add(bindingLabel);
        bindingSelectionPanel.Controls.Add(_enterSendRadioButton);
        bindingSelectionPanel.Controls.Add(_ctrlEnterSendRadioButton);
        bindingSelectionPanel.Controls.Add(_bindingPairLabel);

        _verificationStatusLabel = new Label
        {
            Text = "Not verified. Protected Send stays blocked until this step succeeds.",
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 16, 12, 12),
            ForeColor = Color.DarkBlue
        };

        var buttonsPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            Padding = new Padding(12)
        };

        var buttonsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0)
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

        buttonsFlow.Controls.Add(_verifyFocusedAppButton);
        buttonsFlow.Controls.Add(_skipButton);
        buttonsPanel.Controls.Add(buttonsFlow);

        Controls.Add(bindingSelectionPanel);
        Controls.Add(instructionLabel);
        Controls.Add(instructionLabel2);
        Controls.Add(_verificationStatusLabel);
        Controls.Add(buttonsPanel);

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
        _verificationStatusLabel!.Text = "Switch to the selected app's message composer now. Verification captures focus in 10 seconds; this setup window stays responsive.";
        _verificationStatusLabel.ForeColor = Color.DarkOrange;
        _verifyFocusedAppButton!.Enabled = false;
        if (_setupController is ISetupVerificationProgressReporter setupProgressReporter)
        {
            setupProgressReporter.PublishSetupProgress("waiting_for_focus", "focus_message_composer", binding: selectedSubmit);
        }
        TopMost = false;
        var result = await FocusedProfileVerificationWorker.RunAsync(
            () => focusedSetupController.VerifyFocusedProfile(selectedSubmit, selectedNewline, _layout));

        if (IsDisposed || verificationGeneration != Volatile.Read(ref _verificationGeneration))
        {
            return;
        }

        _verifyFocusedAppButton.Enabled = true;
        TopMost = true;
        Activate();

        if (!result.Succeeded)
        {
            _verificationStatusLabel.Text = "The focused window was not verified. Protected Send remains blocked.";
            _verificationStatusLabel.ForeColor = Color.DarkRed;
            MessageBox.Show(
                "Verification did not confirm the selected composer. Focus the Codex or ChatGPT Desktop message box and try again. Protected Send remains blocked until setup succeeds.",
                "Codex Redaction Gate - Setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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
        _verificationStatusLabel.Text = $"Protected: {profileName}";
        _verificationStatusLabel.ForeColor = Color.DarkGreen;
        _setupCompleted = true;
        Close();
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
