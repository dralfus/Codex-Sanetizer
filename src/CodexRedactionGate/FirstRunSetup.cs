using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
    IReadOnlyDictionary<string, string> Diagnostics);

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

internal sealed class FocusedComposerFirstRunProfileVerifier : IFirstRunProfileVerifier
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
}

internal sealed class FirstRunSetupController : IFirstRunSetupController
{
    private readonly IFirstRunProfileVerifier _profileVerifier;
    private readonly Func<IReadOnlyList<SubmitBindingProfile>, DefaultStorageLayout, IFirstRunSetupController, bool> _showSetupWindow;

    public FirstRunSetupController()
        : this(new FocusedComposerFirstRunProfileVerifier(), ShowSetupWindow)
    {
    }

    internal FirstRunSetupController(
        IFirstRunProfileVerifier profileVerifier,
        Func<IReadOnlyList<SubmitBindingProfile>, DefaultStorageLayout, IFirstRunSetupController, bool>? showSetupWindow = null)
    {
        _profileVerifier = profileVerifier ?? throw new ArgumentNullException(nameof(profileVerifier));
        _showSetupWindow = showSetupWindow ?? ShowSetupWindow;
    }

    public FirstRunSetupResult EnsureSetup(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var initialStatus = GetSetupStatus(layout);
        if (initialStatus.Succeeded && !initialStatus.State.Required)
        {
            SetSetupComplete(layout);
            return new FirstRunSetupResult(
                Succeeded: true,
                Code: "setup_complete",
                State: initialStatus.State,
                Diagnostics: initialStatus.Diagnostics);
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
        var setupCompleted = _showSetupWindow(SetupVisibleProfiles(storeResult.Profiles), layout, this);
        if (setupCompleted)
        {
            var finalStatus = GetSetupStatus(layout);
            if (finalStatus.Succeeded && !finalStatus.State.Required)
            {
                SetSetupComplete(layout);
                return new FirstRunSetupResult(
                    Succeeded: true,
                    Code: "setup_complete_after_window",
                    State: finalStatus.State,
                    Diagnostics: Merge(finalStatus.Diagnostics, new Dictionary<string, string>
                    {
                        ["user_action"] = "setup_window_closed",
                        ["all_profiles_verified"] = "true"
                    }));
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
        var visibleProfiles = SetupVisibleProfiles(storeResult.Profiles);
        var profile = visibleProfiles
            .FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.Ordinal));

        if (profile is null)
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "profile_not_found",
                State: CreateSetupState(visibleProfiles),
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = profileId
                });
        }

        var verifiedProfile = _profileVerifier.Verify(profile);
        var updatedProfiles = visibleProfiles
            .Where(p => !string.Equals(p.ProfileId, profileId, StringComparison.Ordinal))
            .Append(verifiedProfile)
            .OrderBy(p => p.ProfileId, StringComparer.Ordinal)
            .ToArray();
        var batchSaveResult = SubmitBindingProfileStore.Save(layout, updatedProfiles);
        if (!batchSaveResult.Succeeded)
        {
            return new FirstRunSetupResult(
                Succeeded: false,
                Code: "profile_batch_update_failed",
                State: CreateSetupState(visibleProfiles, profileId),
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = profileId,
                    ["save_status"] = batchSaveResult.Code
                });
        }

        var updatedStatus = GetSetupStatus(layout);
        if (!updatedStatus.State.Required)
        {
            SetSetupComplete(layout);
        }

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

    public bool IsSetupComplete(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var setupMarkerPath = Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete");
        return File.Exists(setupMarkerPath);
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
        var visibleProfiles = SetupVisibleProfiles(profiles);
        var unprotected = visibleProfiles
            .Where(p => !p.IsSetupComplete)
            .Select(p => p.ProfileId)
            .ToArray();
        var codexProfile = visibleProfiles.FirstOrDefault(p => string.Equals(p.ProfileId, "codex-desktop", StringComparison.Ordinal));
        var chatGptProfile = visibleProfiles.FirstOrDefault(p => string.Equals(p.ProfileId, "chatgpt-desktop", StringComparison.Ordinal));

        return new FirstRunSetupState(
            Required: unprotected.Length > 0,
            UnprotectedProfileIds: unprotected,
            Status: unprotected.Length == 0 ? "complete" : "pending",
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
        var setupMarkerPath = Path.Combine(layout.SettingsDirectory, ".first_run_setup_complete");
        File.WriteAllText(setupMarkerPath, $"complete:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}");
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
            
            using var form = new FirstRunSetupForm(profiles, layout, setupController);
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
    private readonly IReadOnlyList<SubmitBindingProfile> _profiles;
    private readonly DefaultStorageLayout _layout;
    private readonly IFirstRunSetupController _setupController;
    private bool _setupCompleted;
    private Button? _verifyCodexButton;
    private Button? _verifyChatGptButton;
    private Button? _skipButton;
    private RadioButton? _enterSendRadioButton;
    private RadioButton? _ctrlEnterSendRadioButton;
    private Label? _bindingPairLabel;
    private readonly Dictionary<string, ProfileCardState> _profileCards = new(StringComparer.Ordinal);

    public bool SetupCompleted => _setupCompleted;

    public FirstRunSetupForm(
        IReadOnlyList<SubmitBindingProfile> profiles,
        DefaultStorageLayout layout,
        IFirstRunSetupController setupController)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _setupController = setupController ?? throw new ArgumentNullException(nameof(setupController));

        Text = "First-Time Setup - Codex Redaction Gate";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 700;
        Height = 550;
        MinimizeBox = false;
        MaximizeBox = false;
        TopMost = true;

        InitializeComponents();
    }

    private void InitializeComponents()
    {
        var instructionLabel = new Label
        {
            Text = "Before you can use protected send, you need to verify your AI application profiles.",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 60,
            Padding = new Padding(12, 12, 12, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var instructionLabel2 = new Label
        {
            Text = "Click Verify, focus the matching Codex/ChatGPT composer, and wait until verification completes locally.",
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

        var profilesPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            AutoSize = false
        };

        var profilesFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(0, 0, 0, 12)
        };

        foreach (var profile in _profiles)
        {
            var profileCard = CreateProfileCard(profile);
            _profileCards.Add(profile.ProfileId, profileCard);
            profilesFlow.Controls.Add(profileCard.Container);
        }

        profilesPanel.Controls.Add(profilesFlow);

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

        _verifyCodexButton = new Button
        {
            Text = "Verify Codex Desktop",
            Width = 160,
            Margin = new Padding(0, 0, 8, 0)
        };
        _verifyCodexButton.Click += (_, _) => OnVerifyProfile("codex-desktop");

        _verifyChatGptButton = new Button
        {
            Text = "Verify ChatGPT Desktop",
            Width = 160,
            Margin = new Padding(0, 0, 8, 0)
        };
        _verifyChatGptButton.Click += (_, _) => OnVerifyProfile("chatgpt-desktop");

        _skipButton = new Button
        {
            Text = "Exit setup",
            Width = 140
        };
        _skipButton.Click += (_, _) => OnSkipSetup();

        buttonsFlow.Controls.Add(_verifyCodexButton);
        buttonsFlow.Controls.Add(_verifyChatGptButton);
        buttonsFlow.Controls.Add(_skipButton);
        buttonsPanel.Controls.Add(buttonsFlow);

        Controls.Add(bindingSelectionPanel);
        Controls.Add(instructionLabel);
        Controls.Add(instructionLabel2);
        Controls.Add(profilesPanel);
        Controls.Add(buttonsPanel);

        AcceptButton = _verifyCodexButton;
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

    private ProfileCardState CreateProfileCard(SubmitBindingProfile profile)
    {
        var card = new Panel
        {
            Height = 70,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 8),
            BorderStyle = BorderStyle.FixedSingle
        };

        var profileName = new Label
        {
            Text = profile.ProfileId,
            Font = new Font(FontFamily.GenericSansSerif, 10f, FontStyle.Bold),
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 20
        };

        var statusLabel = new Label
        {
            Text = profile.IsProtected ? "✓ Protected" : "○ Not verified",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 18
        };

        if (profile.IsProtected)
        {
            statusLabel.ForeColor = Color.Green;
        }
        else
        {
            statusLabel.ForeColor = Color.Gray;
        }

        var detailsLabel = new Label
        {
            Text = $"Submit: {profile.SubmitBinding?.DisplayText ?? "N/A"} | Newline: {profile.NewlineBinding?.DisplayText ?? "N/A"}",
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 18,
            ForeColor = Color.DarkGray
        };

        card.Controls.Add(profileName);
        card.Controls.Add(statusLabel);
        card.Controls.Add(detailsLabel);

        return new ProfileCardState(card, statusLabel, detailsLabel);
    }

    private void OnVerifyProfile(string profileId)
    {
        var updatedProfile = _profiles
            .FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.Ordinal));

        if (updatedProfile is not null)
        {
            // Update profile with selected binding pair
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

            var profileWithBinding = updatedProfile with
            {
                SubmitBinding = SubmitKeyBinding.Parse(selectedSubmit).Binding,
                NewlineBinding = SubmitKeyBinding.Parse(selectedNewline).Binding,
                Enabled = true,
                BindingSource = "not_verified",
                CapabilityStatus = OsInteractionStatusIds.BindingUnknown
            };

            // Update UI to show verification in progress
            _profileCards.TryGetValue(profileId, out var card);
            if (card is not null)
            {
                card.StatusLabel.Text = "⟳ Verifying...";
                card.StatusLabel.ForeColor = Color.Orange;
                card.DetailsLabel.Text = $"Submit: {selectedSubmit} | Newline: {selectedNewline}";
                card.DetailsLabel.ForeColor = Color.Black;
            }

            TopMost = false;
            WindowState = FormWindowState.Minimized;
            Hide();
            Application.DoEvents();

            // Save updated profile to store before verification
            var saveResult = SubmitBindingProfileStore.Upsert(_layout, profileWithBinding);
            if (!saveResult.Succeeded)
            {
                MessageBox.Show(
                    $"Failed to save binding preferences. status={saveResult.Code}",
                    "Codex Redaction Gate - Setup required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                Show();
                WindowState = FormWindowState.Normal;
                TopMost = true;
                Activate();
                return;
            }

            var result = _setupController.VerifyProfile(profileId, _layout);

            Show();
            WindowState = FormWindowState.Normal;
            TopMost = true;
            Activate();

            if (card is not null)
            {
                card.StatusLabel.Text = result.Succeeded ? "✓ Protected" : "○ Not verified";
                card.StatusLabel.ForeColor = result.Succeeded ? Color.Green : Color.Gray;
            }

            if (!result.Succeeded)
            {
                MessageBox.Show(
                    $"Verification failed. status={result.Code}. Protected Send will remain blocked until setup succeeds.",
                    "Codex Redaction Gate - Setup required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (!result.State.Required)
            {
                _setupCompleted = true;
                Close();
            }
        }
    }

    private sealed record ProfileCardState(Panel Container, Label StatusLabel, Label DetailsLabel);

    private void OnSkipSetup()
    {
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
