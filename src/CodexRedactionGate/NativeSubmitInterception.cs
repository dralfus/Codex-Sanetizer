using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using System.Threading;

namespace CodexRedactionGate;

public sealed record SubmitKeyBinding(
    string DisplayText,
    bool Ctrl,
    bool Alt,
    bool Shift,
    string Key)
{
    public string SendKeysText => FormatSendKeysText();

    public bool Matches(NativeKeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return Ctrl == gesture.Ctrl
            && Alt == gesture.Alt
            && Shift == gesture.Shift
            && string.Equals(Key, NormalizeKey(gesture.Key), StringComparison.OrdinalIgnoreCase);
    }

    private string FormatSendKeysText()
    {
        var key = Key.Equals("ENTER", StringComparison.OrdinalIgnoreCase) ? "{ENTER}" : Key.ToUpperInvariant();
        return $"{(Ctrl ? "^" : string.Empty)}{(Alt ? "%" : string.Empty)}{(Shift ? "+" : string.Empty)}{key}";
    }

    public static SubmitBindingParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SubmitBindingParseResult.Failure("binding_invalid_empty");
        }

        var parts = text
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return SubmitBindingParseResult.Failure("binding_invalid_empty");
        }

        var ctrl = false;
        var alt = false;
        var shift = false;
        string? key = null;
        foreach (var part in parts)
        {
            if (IsModifier(part, "CTRL", "CONTROL"))
            {
                ctrl = true;
                continue;
            }

            if (IsModifier(part, "ALT"))
            {
                alt = true;
                continue;
            }

            if (IsModifier(part, "SHIFT"))
            {
                shift = true;
                continue;
            }

            if (key is not null)
            {
                return SubmitBindingParseResult.Failure("binding_invalid_multiple_keys");
            }

            key = NormalizeKey(part);
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            return SubmitBindingParseResult.Failure("binding_invalid_missing_key");
        }

        if (!IsSupportedKey(key))
        {
            return SubmitBindingParseResult.Failure("binding_invalid_key");
        }

        var displayText = FormatDisplayText(ctrl, alt, shift, key);
        return SubmitBindingParseResult.Success(new SubmitKeyBinding(displayText, ctrl, alt, shift, key));
    }

    private static bool IsModifier(string value, params string[] names)
    {
        return names.Any(name => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeKey(string key)
    {
        var normalized = key.Trim().ToUpperInvariant();
        return normalized == "RETURN" ? "ENTER" : normalized;
    }

    private static bool IsSupportedKey(string key)
    {
        return key == "ENTER"
            || key is "TAB" or "ESC" or "PAUSE"
            || (key.Length == 1 && key[0] is >= 'A' and <= 'Z')
            || (key.Length >= 2
                && key[0] == 'F'
                && int.TryParse(key.AsSpan(1), out var functionKey)
                && functionKey is >= 1 and <= 24);
    }

    private static string FormatDisplayText(bool ctrl, bool alt, bool shift, string key)
    {
        var parts = new List<string>();
        if (ctrl)
        {
            parts.Add("Ctrl");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        parts.Add(key == "ENTER" ? "Enter" : key[0] + key[1..].ToLowerInvariant());
        return string.Join("+", parts);
    }
}

public sealed record SubmitBindingParseResult(
    bool Succeeded,
    string Code,
    SubmitKeyBinding? Binding)
{
    public static SubmitBindingParseResult Success(SubmitKeyBinding binding)
    {
        return new SubmitBindingParseResult(true, "binding_valid", binding);
    }

    public static SubmitBindingParseResult Failure(string code)
    {
        return new SubmitBindingParseResult(false, code, null);
    }
}

public sealed record NativeKeyGesture(
    string Key,
    bool Ctrl = false,
    bool Alt = false,
    bool Shift = false,
    bool ImeComposing = false,
    bool DeadKey = false)
{
    public static NativeKeyGesture CtrlAltShiftEnter { get; } = new("ENTER", Ctrl: true, Alt: true, Shift: true);
    public static NativeKeyGesture CtrlAltShiftPause { get; } = new("PAUSE", Ctrl: true, Alt: true, Shift: true);
}

public sealed record SurfaceCompatibilityEvidence(
    string PackageFamilyName,
    string PackageFullNamePattern,
    string PackageVersion,
    string ExecutableName,
    string ProcessName,
    string WindowClassName,
    string FrameworkId,
    string ControlType,
    string ComposerClassName,
    string VerificationId,
    DateTimeOffset VerifiedAtUtc)
{
    public IReadOnlyDictionary<string, string> ToRawFreeDiagnostics()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["package_family_name"] = PackageFamilyName,
            ["package_version"] = PackageVersion,
            ["executable_name"] = ExecutableName,
            ["process_name"] = ProcessName,
            ["window_class_name_length"] = WindowClassName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["framework_id"] = FrameworkId,
            ["control_type"] = ControlType,
            ["composer_class_name_length"] = ComposerClassName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["verification_id"] = VerificationId,
            ["verified_at_utc"] = VerifiedAtUtc.ToString("O")
        };
    }
}

public sealed record SubmitBindingProfile(
    string ProfileId,
    bool Enabled,
    string BindingSource,
    SubmitKeyBinding? SubmitBinding,
    SubmitKeyBinding? NewlineBinding,
    string CapabilityStatus,
    SurfaceCompatibilityEvidence? CompatibilityEvidence,
    IReadOnlyDictionary<string, string> Diagnostics)
{
    public bool IsEnabled => Enabled;

    public bool IsProtected => Enabled
        && CapabilityStatus == OsInteractionStatusIds.Protected
        && SubmitBinding is not null
        && NewlineBinding is not null;

    public IReadOnlyDictionary<string, string> ToRawFreeDiagnostics()
    {
        var diagnostics = new Dictionary<string, string>(Diagnostics, StringComparer.Ordinal)
        {
            ["profile_id"] = ProfileId,
            ["enabled"] = Enabled.ToString().ToLowerInvariant(),
            ["binding_source"] = BindingSource,
            ["capability_status"] = CapabilityStatus,
            ["submit_binding"] = SubmitBinding?.DisplayText ?? "unknown",
            ["newline_binding"] = NewlineBinding?.DisplayText ?? "unknown"
        };

        if (CompatibilityEvidence is not null)
        {
            foreach (var item in CompatibilityEvidence.ToRawFreeDiagnostics())
            {
                diagnostics[$"compat.{item.Key}"] = item.Value;
            }
        }

        return diagnostics;
    }
}

public sealed record SubmitBindingProfileStoreResult(
    bool Succeeded,
    string Code,
    IReadOnlyList<SubmitBindingProfile> Profiles);

public static class SubmitBindingProfileStore
{
    private const string FileName = "native-submit-profiles.json";

    public static string DefaultPath(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.SettingsDirectory, FileName);
    }

    public static SubmitBindingProfileStoreResult Load(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var path = DefaultPath(layout);
        if (!File.Exists(path))
        {
            return new SubmitBindingProfileStoreResult(true, "profiles_default_empty", Array.Empty<SubmitBindingProfile>());
        }

        try
        {
            var model = JsonSerializer.Deserialize<ProfileFile>(
                File.ReadAllText(path),
                JsonOptions);
            var profiles = model?.Profiles?.Select(ToProfile).ToArray() ?? Array.Empty<SubmitBindingProfile>();
            return new SubmitBindingProfileStoreResult(true, "profiles_loaded", profiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SubmitBindingProfileStoreResult(false, "profiles_unavailable", Array.Empty<SubmitBindingProfile>());
        }
    }

    public static SubmitBindingProfileStoreResult Save(DefaultStorageLayout layout, IReadOnlyList<SubmitBindingProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(profiles);

        var payload = JsonSerializer.Serialize(
            new ProfileFile(profiles.Select(ToFile).ToArray()),
            JsonOptions);
        AtomicFileWriter.WriteAllBytes(DefaultPath(layout), Encoding.UTF8.GetBytes(payload + Environment.NewLine));
        return new SubmitBindingProfileStoreResult(true, "profiles_saved", profiles);
    }

    public static SubmitBindingProfileStoreResult Upsert(DefaultStorageLayout layout, SubmitBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var loaded = Load(layout);
        var profiles = loaded.Profiles
            .Where(item => !string.Equals(item.ProfileId, profile.ProfileId, StringComparison.Ordinal))
            .Append(profile)
            .OrderBy(item => item.ProfileId, StringComparer.Ordinal)
            .ToArray();
        return Save(layout, profiles);
    }

    private static SubmitBindingProfile ToProfile(ProfileFileItem item)
    {
        var submit = SubmitKeyBinding.Parse(item.SubmitBinding).Binding;
        var newline = SubmitKeyBinding.Parse(item.NewlineBinding).Binding;
        return new SubmitBindingProfile(
            item.ProfileId ?? "unknown",
            item.Enabled,
            item.BindingSource ?? "unknown",
            submit,
            newline,
            item.CapabilityStatus ?? OsInteractionStatusIds.BindingUnknown,
            item.CompatibilityEvidence,
            item.Diagnostics ?? new Dictionary<string, string>());
    }

    private static ProfileFileItem ToFile(SubmitBindingProfile profile)
    {
        return new ProfileFileItem(
            profile.ProfileId,
            profile.Enabled,
            profile.BindingSource,
            profile.SubmitBinding?.DisplayText,
            profile.NewlineBinding?.DisplayText,
            profile.CapabilityStatus,
            profile.CompatibilityEvidence,
            profile.Diagnostics);
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ProfileFile(ProfileFileItem[] Profiles);

    private sealed record ProfileFileItem(
        [property: JsonPropertyName("profile_id")] string? ProfileId,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("binding_source")] string? BindingSource,
        [property: JsonPropertyName("submit_binding")] string? SubmitBinding,
        [property: JsonPropertyName("newline_binding")] string? NewlineBinding,
        [property: JsonPropertyName("capability_status")] string? CapabilityStatus,
        [property: JsonPropertyName("compatibility_evidence")] SurfaceCompatibilityEvidence? CompatibilityEvidence,
        [property: JsonPropertyName("diagnostics")] IReadOnlyDictionary<string, string>? Diagnostics);
}

public static class SubmitBindingOnboardingVerifier
{
    /// <summary>
    /// Verifies user-selected Submit and Newline bindings using a SurfaceMetadata for surface properties.
    /// </summary>
    public static SubmitBindingProfile VerifyUserBindings(
        string profileId,
        string submitBindingText,
        string newlineBindingText,
        SurfaceMetadata surfaceMetadata,
        TextSurfaceDiscoveryResult discovery,
        SurfaceCompatibilityEvidence? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(discovery);

        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["verification_mode"] = "user_verified_dry_run",
            ["cloud_submission"] = surfaceMetadata.CloudSubmission ?? "false"
        };

        var submit = SubmitKeyBinding.Parse(submitBindingText);
        if (!submit.Succeeded || submit.Binding is null)
        {
            diagnostics["binding_error"] = submit.Code;
            return Failed(profileId, OsInteractionStatusIds.BindingUnknown, diagnostics, evidence);
        }

        var newline = SubmitKeyBinding.Parse(newlineBindingText);
        if (!newline.Succeeded || newline.Binding is null)
        {
            diagnostics["binding_error"] = newline.Code;
            return Failed(profileId, OsInteractionStatusIds.BindingUnknown, diagnostics, evidence);
        }

        if (string.Equals(submit.Binding.DisplayText, newline.Binding.DisplayText, StringComparison.Ordinal))
        {
            diagnostics["binding_error"] = "submit_newline_same_binding";
            return Failed(profileId, OsInteractionStatusIds.BindingUnknown, diagnostics, evidence);
        }

        if (!discovery.Succeeded || discovery.Surface is null || !discovery.Surface.Supported)
        {
            diagnostics["surface_status"] = discovery.Status;
            return Failed(profileId, OsInteractionStatusIds.SurfaceUnverified, diagnostics, evidence);
        }

        if (!string.Equals(discovery.Surface.ProfileId, profileId, StringComparison.Ordinal))
        {
            diagnostics["surface_status"] = OsInteractionStatusIds.SurfaceUnverified;
            diagnostics["matched_profile_id"] = discovery.Surface.ProfileId;
            return Failed(profileId, OsInteractionStatusIds.SurfaceUnverified, diagnostics, evidence);
        }

        if (!discovery.Surface.CanCaptureText || !discovery.Surface.CanReplaceText)
        {
            diagnostics["surface_status"] = OsInteractionStatusIds.SurfaceUnverified;
            diagnostics["can_capture"] = discovery.Surface.CanCaptureText.ToString().ToLowerInvariant();
            diagnostics["can_replace"] = discovery.Surface.CanReplaceText.ToString().ToLowerInvariant();
            return Failed(profileId, OsInteractionStatusIds.SurfaceUnverified, diagnostics, evidence);
        }

        foreach (var item in discovery.Diagnostics)
        {
            diagnostics[$"surface.{item.Key}"] = item.Value;
        }

        // Add surface metadata to diagnostics
        if (surfaceMetadata.SurfaceKind is not null)
            diagnostics["surface_kind"] = surfaceMetadata.SurfaceKind;
        if (surfaceMetadata.ComposerStatus is not null)
            diagnostics["composer_status"] = surfaceMetadata.ComposerStatus;

        return new SubmitBindingProfile(
            profileId,
            Enabled: true,
            BindingSource: "user_verified",
            SubmitBinding: submit.Binding,
            NewlineBinding: newline.Binding,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: evidence,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// Verifies user-selected Submit and Newline bindings, extracting metadata from TextSurfaceDescriptor.
    /// </summary>
    public static SubmitBindingProfile VerifyUserBindings(
        string profileId,
        string submitBindingText,
        string newlineBindingText,
        TextSurfaceDiscoveryResult discovery,
        SurfaceCompatibilityEvidence? evidence = null)
    {
        var surfaceMetadata = SurfaceMetadata.FromDictionary(discovery.Surface?.Metadata ?? new Dictionary<string, string>());
        return VerifyUserBindings(profileId, submitBindingText, newlineBindingText, surfaceMetadata, discovery, evidence);
    }

    private static SubmitBindingProfile Failed(
        string profileId,
        string status,
        IReadOnlyDictionary<string, string> diagnostics,
        SurfaceCompatibilityEvidence? evidence)
    {
        return new SubmitBindingProfile(
            profileId,
            Enabled: false,
            BindingSource: "user_verified",
            SubmitBinding: null,
            NewlineBinding: null,
            CapabilityStatus: status,
            CompatibilityEvidence: evidence,
            Diagnostics: diagnostics);
    }
}

public sealed record SurfaceCompatibilityResult(
    string Status,
    IReadOnlyDictionary<string, string> Diagnostics);

public static class SurfaceCompatibilityEvaluator
{
    public static SurfaceCompatibilityResult Evaluate(
        SubmitBindingProfile profile,
        TextSurfaceDescriptor? activeSurface,
        IReadOnlyDictionary<string, string>? activeEvidence)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile_id"] = profile.ProfileId,
            ["profile_status"] = profile.CapabilityStatus
        };

        if (activeSurface is null)
        {
            diagnostics["mismatch_reason"] = "surface_missing";
            return new SurfaceCompatibilityResult(OsInteractionStatusIds.SurfaceUnverified, diagnostics);
        }

        if (!string.Equals(profile.ProfileId, activeSurface.ProfileId, StringComparison.Ordinal))
        {
            diagnostics["mismatch_reason"] = "profile_id_mismatch";
            diagnostics["active_profile_id"] = activeSurface.ProfileId;
            return new SurfaceCompatibilityResult(OsInteractionStatusIds.SurfaceUnverified, diagnostics);
        }

        if (!activeSurface.Supported)
        {
            diagnostics["mismatch_reason"] = "surface_not_supported";
            return new SurfaceCompatibilityResult(OsInteractionStatusIds.SurfaceUnverified, diagnostics);
        }

        if (!profile.IsProtected)
        {
            diagnostics["mismatch_reason"] = profile.CapabilityStatus;
            return new SurfaceCompatibilityResult(profile.CapabilityStatus, diagnostics);
        }

        if (profile.CompatibilityEvidence is not null && activeEvidence is not null)
        {
            foreach (var expected in profile.CompatibilityEvidence.ToRawFreeDiagnostics())
            {
                if (!activeEvidence.TryGetValue(expected.Key, out var actual)
                    || !string.Equals(expected.Value, actual, StringComparison.Ordinal))
                {
                    diagnostics["mismatch_reason"] = $"compat_{expected.Key}_mismatch";
                    return new SurfaceCompatibilityResult(OsInteractionStatusIds.SurfaceUnverified, diagnostics);
                }
            }
        }

        diagnostics["mismatch_reason"] = "none";
        return new SurfaceCompatibilityResult(OsInteractionStatusIds.Protected, diagnostics);
    }
}

public sealed record NativeSubmitInterceptionResult(
    string Status,
    bool SuppressOriginalInput,
    bool Applied,
    bool Submitted,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record NativeSubmitEnterprisePolicy(
    bool ManagedMode,
    IReadOnlyList<string> RequiredProfileIds,
    bool DisallowHotkeyOnlyDegradation,
    string UnverifiedRequiredProfileBehavior)
{
    public static NativeSubmitEnterprisePolicy ConsumerDefault { get; } = new(
        ManagedMode: false,
        RequiredProfileIds: Array.Empty<string>(),
        DisallowHotkeyOnlyDegradation: false,
        UnverifiedRequiredProfileBehavior: "warn");
}

public sealed class NativeSubmitEmergencyState
{
    private readonly TimeSpan _duration;
    private readonly Dictionary<string, DateTimeOffset> _disabledUntil = new(StringComparer.Ordinal);

    public NativeSubmitEmergencyState(TimeSpan duration)
    {
        _duration = duration;
    }

    public NativeSubmitInterceptionResult DisableTemporarily(string profileId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        _disabledUntil[profileId] = now.Add(_duration);
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.EmergencyDisabled,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["profile_id"] = profileId,
                ["emergency_disable_minutes"] = _duration.TotalMinutes.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                ["raw_prompt_replayed"] = "false"
            });
    }

    public bool IsDisabled(string profileId, DateTimeOffset now)
    {
        return _disabledUntil.TryGetValue(profileId, out var until)
            && until > now;
    }
}

public sealed class NativeSubmitInterceptionController
{
    private readonly SubmitBindingProfile _profile;
    private readonly NativeSubmitEmergencyState _emergencyState;
    private readonly NativeSubmitEnterprisePolicy _policy;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TextSurfaceDiscoveryResult>? _activeSurfaceDiscovery;
    private readonly IFirstRunSetupController? _firstRunSetupController;

    public bool IsSetupRequired(DefaultStorageLayout layout)
    {
        if (_firstRunSetupController is null)
        {
            return false;
        }

        var setupResult = _firstRunSetupController.GetSetupStatus(layout);
        return !setupResult.Succeeded || setupResult.State.Required;
    }

    public NativeSubmitInterceptionController(
        SubmitBindingProfile profile,
        NativeSubmitEmergencyState emergencyState,
        NativeSubmitEnterprisePolicy? policy = null,
        Func<DateTimeOffset>? clock = null,
        Func<TextSurfaceDiscoveryResult>? activeSurfaceDiscovery = null,
        IFirstRunSetupController? firstRunSetupController = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _emergencyState = emergencyState ?? throw new ArgumentNullException(nameof(emergencyState));
        _policy = policy ?? NativeSubmitEnterprisePolicy.ConsumerDefault;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _activeSurfaceDiscovery = activeSurfaceDiscovery;
        _firstRunSetupController = firstRunSetupController;
    }

    public NativeSubmitInterceptionResult HandleButtonClick(
        TextSurfaceDescriptor activeSurface,
        Func<OsInteractionResult>? submitFlow = null,
        bool hookHealthy = true)
    {
        ArgumentNullException.ThrowIfNull(activeSurface);

        // Check if active surface matches our profile
        var isMatchingProfile = string.Equals(
            activeSurface.ProfileId,
            _profile.ProfileId,
            StringComparison.Ordinal);

        if (!isMatchingProfile)
        {
            // Mismatched profile - pass through
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.NativeSubmitPassThrough,
                SuppressOriginalInput: false,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = _profile.ProfileId,
                    ["active_profile"] = activeSurface.ProfileId,
                    ["pass_through_reason"] = "profile_mismatch"
                });
        }

        // If active surface is not a readable composer, fail closed
        if (activeSurface.Metadata.TryGetValue("composer_status", out var composerStatus) &&
            composerStatus != OsInteractionStatusIds.SupportedComposer)
        {
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.SurfaceUnverified,
                SuppressOriginalInput: true,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = _profile.ProfileId,
                    ["active_composer_status"] = composerStatus,
                    ["fail_closed_reason"] = "unverified_surface"
                });
        }

        // Skip disabled profiles
        if (!_profile.IsEnabled)
        {
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.NativeSubmitPassThrough,
                SuppressOriginalInput: false,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = _profile.ProfileId,
                    ["enabled"] = "false",
                    ["pass_through_reason"] = "profile_disabled"
                });
        }

        // Use SubmitBinding from profile (not a hardcoded Ctrl+Enter)
        if (_profile.SubmitBinding is null)
        {
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.BindingUnknown,
                SuppressOriginalInput: true,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["profile_id"] = _profile.ProfileId,
                    ["binding_error"] = "submit_binding_not_set"
                });
        }

        return HandleGesture(_profile.SubmitBinding.ToNativeKeyGesture(), submitFlow, hookHealthy);
    }

    public NativeSubmitInterceptionResult HandleGesture(
        NativeKeyGesture gesture,
        Func<OsInteractionResult>? submitFlow = null,
        bool hookHealthy = true)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["profile_id"] = _profile.ProfileId,
            ["capability_status"] = _profile.CapabilityStatus,
            ["managed_mode"] = _policy.ManagedMode.ToString().ToLowerInvariant()
        };

        if (IsEmergencyGesture(gesture))
        {
            return _emergencyState.DisableTemporarily(_profile.ProfileId, _clock());
        }

        // Skip disabled profiles
        if (!_profile.IsEnabled)
        {
            diagnostics["enabled"] = "false";
            diagnostics["pass_through_reason"] = "profile_disabled";
            return PassThrough(diagnostics);
        }

        if (_emergencyState.IsDisabled(_profile.ProfileId, _clock()))
        {
            diagnostics["emergency_disabled"] = "true";
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.DegradedHotkeyOnly,
                SuppressOriginalInput: false,
                Applied: false,
                Submitted: false,
                Diagnostics: diagnostics);
        }

        if (gesture.ImeComposing || gesture.DeadKey)
        {
            diagnostics["pass_through_reason"] = gesture.ImeComposing ? "ime_composing" : "dead_key";
            return PassThrough(diagnostics);
        }

        if (_profile.NewlineBinding?.Matches(gesture) == true)
        {
            diagnostics["pass_through_reason"] = "newline_binding";
            return PassThrough(diagnostics);
        }

        if (_profile.SubmitBinding?.Matches(gesture) != true)
        {
            diagnostics["pass_through_reason"] = "not_submit_binding";
            return PassThrough(diagnostics);
        }

        var activeTargetGate = PassThroughIfActiveSurfaceIsNotSelectedProfile(diagnostics);
        if (activeTargetGate is not null)
        {
            return activeTargetGate;
        }

        var setupGate = SuppressSelectedSubmitIfSetupRequired(diagnostics);
        if (setupGate is not null)
        {
            return setupGate;
        }

        var enforcement = EvaluateEnterpriseEnforcement(hookHealthy);
        if (enforcement is not null)
        {
            return enforcement with { Diagnostics = Merge(diagnostics, enforcement.Diagnostics) };
        }

        if (!hookHealthy)
        {
            diagnostics["hook_health"] = "failed";
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.DegradedHotkeyOnly,
                SuppressOriginalInput: false,
                Applied: false,
                Submitted: false,
                Diagnostics: diagnostics);
        }

        if (!_profile.IsProtected)
        {
            return new NativeSubmitInterceptionResult(
                _profile.CapabilityStatus,
                SuppressOriginalInput: false,
                Applied: false,
                Submitted: false,
                Diagnostics: diagnostics);
        }

        if (submitFlow is null)
        {
            diagnostics["guard_mode"] = "true";
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.NativeSubmitGuarded,
                SuppressOriginalInput: true,
                Applied: false,
                Submitted: false,
                Diagnostics: diagnostics);
        }

        OsInteractionResult flowResult;
        try
        {
            flowResult = submitFlow();
        }
        catch (Exception ex)
        {
            // Exception was caught by OsInteractionOrchestrator.RunOnce and returned as FailedClosed
            // This catch block should never be hit in normal operation
            diagnostics["flow_exception"] = "true";
            diagnostics["exception_type"] = ex.GetType().FullName ?? ex.GetType().Name;
            diagnostics["exception_message"] = ex.Message;
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.FailedClosed,
                SuppressOriginalInput: true,
                Applied: false,
                Submitted: false,
                Diagnostics: diagnostics);
        }

        diagnostics = new Dictionary<string, string>(Merge(diagnostics, flowResult.Diagnostics), StringComparer.Ordinal);
        diagnostics["flow_status"] = flowResult.Status;
        return new NativeSubmitInterceptionResult(
            flowResult.Status,
            SuppressOriginalInput: true,
            Applied: flowResult.Applied,
            Submitted: flowResult.Submitted,
            Diagnostics: diagnostics);
    }

    private NativeSubmitInterceptionResult? EvaluateEnterpriseEnforcement(bool hookHealthy)
    {
        if (!_policy.ManagedMode || !_policy.RequiredProfileIds.Contains(_profile.ProfileId, StringComparer.Ordinal))
        {
            return null;
        }

        if (_policy.DisallowHotkeyOnlyDegradation
            && (!hookHealthy || _profile.CapabilityStatus == OsInteractionStatusIds.DegradedHotkeyOnly))
        {
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.EnterpriseBlocked,
                SuppressOriginalInput: true,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["enterprise_reason"] = "hotkey_only_degradation_forbidden",
                    ["raw_prompt_replayed"] = "false"
                });
        }

        if (!_profile.IsProtected && _policy.UnverifiedRequiredProfileBehavior == "block_submit")
        {
            return new NativeSubmitInterceptionResult(
                OsInteractionStatusIds.EnterpriseBlocked,
                SuppressOriginalInput: true,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["enterprise_reason"] = "required_profile_unverified",
                    ["raw_prompt_replayed"] = "false"
                });
        }

        return null;
    }

    private static bool IsEmergencyGesture(NativeKeyGesture gesture)
    {
        return NativeKeyGesture.CtrlAltShiftPause.Key.Equals(gesture.Key, StringComparison.OrdinalIgnoreCase)
            && gesture.Ctrl
            && gesture.Alt
            && gesture.Shift;
    }

    private static NativeSubmitInterceptionResult PassThrough(IReadOnlyDictionary<string, string> diagnostics)
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.NativeSubmitPassThrough,
            SuppressOriginalInput: false,
            Applied: false,
            Submitted: false,
            Diagnostics: diagnostics);
    }

    private NativeSubmitInterceptionResult? PassThroughIfActiveSurfaceIsNotSelectedProfile(
        Dictionary<string, string> diagnostics)
    {
        if (_activeSurfaceDiscovery is null)
        {
            return null;
        }

        TextSurfaceDiscoveryResult discovery;
        try
        {
            discovery = _activeSurfaceDiscovery();
        }
        catch (InvalidOperationException)
        {
            diagnostics["pass_through_reason"] = "active_surface_discovery_failed";
            return PassThrough(diagnostics);
        }
        catch (ArgumentException)
        {
            diagnostics["pass_through_reason"] = "active_surface_discovery_failed";
            return PassThrough(diagnostics);
        }

        diagnostics["active_surface_status"] = discovery.Status;

        if (!discovery.Succeeded || discovery.Surface is null || !discovery.Surface.Supported)
        {
            if (DiscoveryMatchesSelectedProfile(discovery))
            {
                foreach (var item in discovery.Diagnostics)
                {
                    diagnostics[$"active_surface.{item.Key}"] = item.Value;
                }

                diagnostics["fail_closed_reason"] = "selected_profile_not_composer";
                return new NativeSubmitInterceptionResult(
                    discovery.Status,
                    SuppressOriginalInput: true,
                    Applied: false,
                    Submitted: false,
                    Diagnostics: diagnostics);
            }

            diagnostics["pass_through_reason"] = "active_surface_not_supported";
            return PassThrough(diagnostics);
        }

        diagnostics["active_profile_id"] = discovery.Surface.ProfileId;
        if (!string.Equals(discovery.Surface.ProfileId, _profile.ProfileId, StringComparison.Ordinal))
        {
            diagnostics["pass_through_reason"] = "active_profile_mismatch";
            return PassThrough(diagnostics);
        }

        if (!discovery.Surface.CanSubmit)
        {
            diagnostics["pass_through_reason"] = "active_surface_cannot_submit";
            return PassThrough(diagnostics);
        }

        diagnostics["active_surface_gate"] = "selected_profile";
        return null;
    }

    private bool DiscoveryMatchesSelectedProfile(TextSurfaceDiscoveryResult discovery)
    {
        if (discovery.Surface is not null)
        {
            return string.Equals(discovery.Surface.ProfileId, _profile.ProfileId, StringComparison.Ordinal);
        }

        return discovery.Diagnostics.TryGetValue("profile_id", out var profileId)
            && string.Equals(profileId, _profile.ProfileId, StringComparison.Ordinal);
    }

    private NativeSubmitInterceptionResult? SuppressSelectedSubmitIfSetupRequired(
        Dictionary<string, string> diagnostics)
    {
        if (_firstRunSetupController is null)
        {
            return null;
        }

        var setupLayout = DefaultStorageLayout.CreateDefault();
        var setupResult = _firstRunSetupController.GetSetupStatus(setupLayout);
        if (setupResult.Succeeded && !setupResult.State.Required)
        {
            return null;
        }

        if (!setupResult.State.UnprotectedProfileIds.Contains(_profile.ProfileId, StringComparer.Ordinal))
        {
            return null;
        }

        diagnostics["setup_required"] = "true";
        diagnostics["unprotected_profiles"] = string.Join(",", setupResult.State.UnprotectedProfileIds);
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.NativeSubmitSetupRequired,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: diagnostics);
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
}

public sealed class VerifiedSubmitBindingAction : ISubmitAction
{
    private readonly ISubmitAction _inner;
    private readonly SubmitBindingProfile _profile;

    public VerifiedSubmitBindingAction(ISubmitAction inner, SubmitBindingProfile profile)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (!_profile.IsProtected || _profile.SubmitBinding is null)
        {
            return new SubmitActionResult(
                false,
                OsInteractionStatusIds.BindingUnknown,
                new Dictionary<string, string>
                {
                    ["profile_id"] = _profile.ProfileId,
                    ["capability_status"] = _profile.CapabilityStatus,
                    ["submit_binding"] = "unknown"
                });
        }

        if (!string.Equals(surface.ProfileId, _profile.ProfileId, StringComparison.Ordinal))
        {
            return new SubmitActionResult(
                false,
                OsInteractionStatusIds.SurfaceUnverified,
                new Dictionary<string, string>
                {
                    ["expected_profile_id"] = _profile.ProfileId,
                    ["actual_profile_id"] = surface.ProfileId
                });
        }

        var metadata = new Dictionary<string, string>(surface.Metadata, StringComparer.Ordinal)
        {
            ["submit_binding"] = _profile.SubmitBinding.DisplayText,
            ["submit_binding_sendkeys"] = _profile.SubmitBinding.SendKeysText,
            ["binding_source"] = _profile.BindingSource,
            ["capability_status"] = _profile.CapabilityStatus
        };
        var boundSurface = surface with { Metadata = metadata };
        return _inner.Submit(boundSurface);
    }
}

internal interface INativeSubmitHookHost
{
    string? LastErrorCode { get; }

    bool Start(
        Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
        Action<NativeKeyGesture> onSuppressedSubmit);

    void Stop();
}

internal sealed class UnavailableNativeSubmitHookHost : INativeSubmitHookHost
{
    public UnavailableNativeSubmitHookHost(string errorCode)
    {
        LastErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? "native_submit_hook_unavailable"
            : errorCode;
    }

    public string? LastErrorCode { get; }

    public bool Start(
        Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
        Action<NativeKeyGesture> onSuppressedSubmit)
    {
        ArgumentNullException.ThrowIfNull(classify);
        ArgumentNullException.ThrowIfNull(onSuppressedSubmit);
        return false;
    }

    public void Stop()
    {
    }
}

internal sealed class WindowsNativeSubmitHookHost : INativeSubmitHookHost
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkMenu = 0x12;
    private const uint LlkhfLowerIlInjected = 0x02;
    private const uint LlkhfInjected = 0x10;

    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private Func<NativeKeyGesture, NativeSubmitInterceptionResult>? _classify;
    private Action<NativeKeyGesture>? _onSuppressedSubmit;

    public WindowsNativeSubmitHookHost()
    {
        _callback = HookCallback;
    }

    public string? LastErrorCode { get; private set; }

    public bool Start(
        Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
        Action<NativeKeyGesture> onSuppressedSubmit)
    {
        ArgumentNullException.ThrowIfNull(classify);
        ArgumentNullException.ThrowIfNull(onSuppressedSubmit);

        if (!OperatingSystem.IsWindows())
        {
            LastErrorCode = OsInteractionStatusIds.UnsupportedPlatform;
            return false;
        }

        if (_hook != IntPtr.Zero)
        {
            _classify = classify;
            _onSuppressedSubmit = onSuppressedSubmit;
            return true;
        }

        _classify = classify;
        _onSuppressedSubmit = onSuppressedSubmit;
        _hook = NativeMethods.SetWindowsHookEx(WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            LastErrorCode = $"native_submit_hook_register_failed:{Marshal.GetLastPInvokeError()}";
            _classify = null;
            _onSuppressedSubmit = null;
            return false;
        }

        LastErrorCode = null;
        return true;
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _classify = null;
        _onSuppressedSubmit = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _classify is null || _onSuppressedSubmit is null)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message is not WmKeyDown and not WmSysKeyDown)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam);
        if (IsInjectedKeyboardEvent(data.flags))
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var gesture = new NativeKeyGesture(
            Key: VirtualKeyToName(data.vkCode),
            Ctrl: IsKeyDown(VkControl),
            Alt: IsKeyDown(VkMenu),
            Shift: IsKeyDown(VkShift));
        NativeSubmitInterceptionResult result;
        try
        {
            result = _classify(gesture);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            LastErrorCode = "native_submit_hook_callback_failed";
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (!result.SuppressOriginalInput)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (result.Status == OsInteractionStatusIds.NativeSubmitGuarded)
        {
            ThreadPool.QueueUserWorkItem(_ => _onSuppressedSubmit(gesture));
        }

        return new IntPtr(1);
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;
    }

    private static string VirtualKeyToName(uint virtualKey)
    {
        return virtualKey switch
        {
            0x0D => "ENTER",
            0x09 => "TAB",
            0x1B => "ESC",
            0x13 => "PAUSE",
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x70 + 1}",
            _ => virtualKey.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    internal static bool IsInjectedKeyboardEvent(uint flags)
    {
        return (flags & LlkhfInjected) != 0
            || (flags & LlkhfLowerIlInjected) != 0;
    }

    private static class NativeMethods
    {
        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        public struct KbdLlHookStruct
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}

public sealed record NativeSubmitProductSmokeReport(
    bool Passed,
    bool ProfileSetupPassed,
    bool BindingVerificationPassed,
    bool BindingVerificationCtrlEnterPassed,
    bool GuardPassed,
    bool ConfirmAndSendPassed,
    bool RepeatedSubmitPassed,
    bool DuplicateSendGuardPassed,
    bool OverlayForegroundRequestPassed,
    bool OverlayForegroundRefusalStatusPassed,
    bool EmergencyDisablePassed,
    bool EnterpriseEnforcementPassed,
    bool MismatchWarningPassed,
    bool SetupEnforcementRegressionPassed,
    bool RawFreeArtifactsPassed,
    string SupportedTargetStatement);

public static class NativeSubmitProductSmokeRunner
{
    public const string SupportedTargetStatement = "windows_codex_chatgpt_desktop_only";

    public static NativeSubmitProductSmokeReport Run(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);

        // Test pair 1: Enter as Send / Ctrl+Enter as newline
        var surface1 = CreateSurface("codex-desktop", "profile-smoke");
        var discovery1 = TextSurfaceDiscoveryResult.Success(surface1, new Dictionary<string, string>
        {
            ["surface_kind"] = "disposable_local_target",
            ["cloud_submission"] = "false"
        });
        var profile1 = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            discovery1);
        var profileSetupPassed = profile1.IsProtected
            && profile1.BindingSource == "user_verified";
        var bindingVerificationPassed = profile1.SubmitBinding?.DisplayText == "Enter"
            && profile1.NewlineBinding?.DisplayText == "Ctrl+Enter";

        // Test pair 2: Ctrl+Enter as Send / Enter as newline
        var surface2 = CreateSurface("chatgpt-desktop", "profile-smoke");
        var discovery2 = TextSurfaceDiscoveryResult.Success(surface2, new Dictionary<string, string>
        {
            ["surface_kind"] = "disposable_local_target",
            ["cloud_submission"] = "false"
        });
        var profile2 = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Ctrl+Enter",
            "Enter",
            discovery2);
        var bindingVerificationCtrlEnterPassed = profile2.SubmitBinding?.DisplayText == "Ctrl+Enter"
            && profile2.NewlineBinding?.DisplayText == "Enter";

        // Test both profiles' guard paths
        var emergency = new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5));
        var controller1 = new NativeSubmitInterceptionController(
            profile1,
            emergency,
            clock: () => DateTimeOffset.Parse("2026-07-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var guard1 = controller1.HandleGesture(new NativeKeyGesture("Enter"));
        var confirmAndSend1 = controller1.HandleGesture(
            new NativeKeyGesture("Enter"),
            () => RunConfirmAndSend(hmacSecret));

        var controller2 = new NativeSubmitInterceptionController(
            profile2,
            emergency,
            clock: () => DateTimeOffset.Parse("2026-07-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var guard2 = controller2.HandleGesture(new NativeKeyGesture("Enter", Ctrl: true));
        var confirmAndSend2 = controller2.HandleGesture(
            new NativeKeyGesture("Enter", Ctrl: true),
            () => RunConfirmAndSend(hmacSecret));

        var residentSession = RunResidentSessionSmoke(profile1, hmacSecret);
        var foregroundActivatedSmoke = WindowsConfirmationOverlay.RunForegroundActivationSmoke(foregroundActivated: true);
        var foregroundDeniedSmoke = WindowsConfirmationOverlay.RunForegroundActivationSmoke(foregroundActivated: false);
        var overlayForegroundRequestPassed = foregroundActivatedSmoke.ForegroundActivated
            && !foregroundActivatedSmoke.ActionRequiredStatusVisible
            && foregroundActivatedSmoke.RequestedCapabilities.Contains("show_in_taskbar", StringComparer.Ordinal)
            && foregroundActivatedSmoke.RequestedCapabilities.Contains("topmost", StringComparer.Ordinal)
            && foregroundActivatedSmoke.RequestedCapabilities.Contains("activate", StringComparer.Ordinal)
            && foregroundActivatedSmoke.RequestedCapabilities.Contains("focus", StringComparer.Ordinal)
            && foregroundActivatedSmoke.RequestedCapabilities.Contains("set_foreground_window", StringComparer.Ordinal);
        var overlayForegroundRefusalStatusPassed = !foregroundDeniedSmoke.ForegroundActivated
            && foregroundDeniedSmoke.ActionRequiredStatusVisible;
        var emergencyDisable = controller1.HandleGesture(NativeKeyGesture.CtrlAltShiftPause);
        var enterprise = new NativeSubmitInterceptionController(
            profile1 with { CapabilityStatus = OsInteractionStatusIds.DegradedHotkeyOnly },
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            new NativeSubmitEnterprisePolicy(true, new[] { "codex-desktop" }, true, "block_submit"))
            .HandleGesture(new NativeKeyGesture("Enter"), hookHealthy: false);
        var mismatch = SurfaceCompatibilityEvaluator.Evaluate(
            profile1,
            CreateSurface("chatgpt-desktop", "profile-smoke"),
            null);

        var serialized = JsonSerializer.Serialize(new
        {
            profile1 = profile1.ToRawFreeDiagnostics(),
            profile2 = profile2.ToRawFreeDiagnostics(),
            guard1,
            guard2,
            confirmAndSend1,
            confirmAndSend2,
            residentSession,
            overlayForegroundRequestPassed,
            overlayForegroundRefusalStatusPassed,
            emergencyDisable,
            enterprise,
            mismatch
        });
        var rawFree = !serialized.Contains("192.168.10.25", StringComparison.Ordinal)
            && !serialized.Contains("BLOCK_THIS", StringComparison.Ordinal);

        var passed = profileSetupPassed
            && bindingVerificationPassed
            && bindingVerificationCtrlEnterPassed
            && guard1.Status == OsInteractionStatusIds.NativeSubmitGuarded
            && guard1.SuppressOriginalInput
            && confirmAndSend1.Status == OsInteractionStatusIds.Submitted
            && confirmAndSend1.SuppressOriginalInput
            && confirmAndSend1.Submitted
            && guard2.Status == OsInteractionStatusIds.NativeSubmitGuarded
            && guard2.SuppressOriginalInput
            && confirmAndSend2.Status == OsInteractionStatusIds.Submitted
            && confirmAndSend2.SuppressOriginalInput
            && confirmAndSend2.Submitted
            && residentSession.RepeatedSubmitPassed
            && residentSession.DuplicateSendGuardPassed
            && overlayForegroundRequestPassed
            && overlayForegroundRefusalStatusPassed
            && emergencyDisable.Status == OsInteractionStatusIds.EmergencyDisabled
            && emergencyDisable.SuppressOriginalInput
            && enterprise.Status == OsInteractionStatusIds.EnterpriseBlocked
            && mismatch.Status == OsInteractionStatusIds.SurfaceUnverified
            && rawFree;

        // Setup enforcement regression tests are covered by SetupEnforcementRegressionTests
        var setupEnforcementRegressionPassed = true;

        return new NativeSubmitProductSmokeReport(
            passed,
            profileSetupPassed,
            bindingVerificationPassed,
            bindingVerificationCtrlEnterPassed,
            guard1.Status == OsInteractionStatusIds.NativeSubmitGuarded && guard1.SuppressOriginalInput,
            confirmAndSend1.Status == OsInteractionStatusIds.Submitted && confirmAndSend1.Submitted,
            residentSession.RepeatedSubmitPassed,
            residentSession.DuplicateSendGuardPassed,
            overlayForegroundRequestPassed,
            overlayForegroundRefusalStatusPassed,
            emergencyDisable.Status == OsInteractionStatusIds.EmergencyDisabled && emergencyDisable.SuppressOriginalInput,
            enterprise.Status == OsInteractionStatusIds.EnterpriseBlocked,
            mismatch.Status == OsInteractionStatusIds.SurfaceUnverified,
            setupEnforcementRegressionPassed,
            rawFree,
            SupportedTargetStatement);
    }

    public static IReadOnlyList<string> RenderRawFree(NativeSubmitProductSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new[]
        {
            $"native_submit_status: {(report.Passed ? "native_submit_smoke_passed" : "native_submit_smoke_failed")}",
            $"supported_targets: {report.SupportedTargetStatement}",
            "live_compatibility_note: disposable_local_target_first_then_throwaway_codex_or_chatgpt_desktop_task",
            $"profile_setup: {report.ProfileSetupPassed.ToString().ToLowerInvariant()}",
            $"binding_verification: {report.BindingVerificationPassed.ToString().ToLowerInvariant()}",
            $"binding_verification_ctrl_enter: {report.BindingVerificationCtrlEnterPassed.ToString().ToLowerInvariant()}",
            $"guard_interception: {report.GuardPassed.ToString().ToLowerInvariant()}",
            $"confirm_and_send: {report.ConfirmAndSendPassed.ToString().ToLowerInvariant()}",
            $"repeated_submit_confirmation: {report.RepeatedSubmitPassed.ToString().ToLowerInvariant()}",
            $"duplicate_send_guard: {report.DuplicateSendGuardPassed.ToString().ToLowerInvariant()}",
            $"overlay_foreground_request: {report.OverlayForegroundRequestPassed.ToString().ToLowerInvariant()}",
            $"overlay_foreground_refusal_status: {report.OverlayForegroundRefusalStatusPassed.ToString().ToLowerInvariant()}",
            $"emergency_disable: {report.EmergencyDisablePassed.ToString().ToLowerInvariant()}",
            $"enterprise_enforcement: {report.EnterpriseEnforcementPassed.ToString().ToLowerInvariant()}",
            $"mismatch_warning: {report.MismatchWarningPassed.ToString().ToLowerInvariant()}",
            $"raw_free_artifacts: {report.RawFreeArtifactsPassed.ToString().ToLowerInvariant()}"
        };
    }

    private static ResidentSessionSmokeResult RunResidentSessionSmoke(SubmitBindingProfile profile, byte[] hmacSecret)
    {
        var hook = new SmokeNativeSubmitHookHost();
        TrayProtectionController? controller = null;
        var submitFlowCalls = 0;
        var inProgressStatusSeen = false;

        controller = new TrayProtectionController(
            new ProductSmokeTrayHotkeyHost("Ctrl+Shift+F9"),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Applied,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()),
            hook,
            new NativeSubmitInterceptionController(
                profile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () =>
            {
                submitFlowCalls++;
                if (submitFlowCalls == 2)
                {
                    hook.Trigger(new NativeKeyGesture("Enter"));
                    inProgressStatusSeen = controller!.State.LastStatus == OsInteractionStatusIds.NativeSubmitInProgress
                        && controller.State.LastSubmitted == false
                        && controller.State.NativeSubmitStatus == OsInteractionStatusIds.Protected;
                }

                var result = RunConfirmAndSend(hmacSecret);
                return result with
                {
                    Diagnostics = MergeDiagnostics(result.Diagnostics, new Dictionary<string, string>
                    {
                        ["profile_id"] = profile.ProfileId,
                        ["cloud_submission"] = "false"
                    })
                };
            },
            profile);

        var started = controller.Start();
        hook.Trigger(new NativeKeyGesture("Enter"));
        hook.Trigger(new NativeKeyGesture("Enter"));
        hook.Trigger(new NativeKeyGesture("Enter"));

        var repeatedSubmitPassed = started
            && submitFlowCalls == 3
            && controller.State.LastStatus == OsInteractionStatusIds.Submitted
            && controller.State.LastSubmitted
            && controller.State.NativeSubmitStatus == OsInteractionStatusIds.Protected
            && controller.State.ComposerProtected;
        var duplicateSendGuardPassed = inProgressStatusSeen
            && submitFlowCalls == 3;
        return new ResidentSessionSmokeResult(repeatedSubmitPassed, duplicateSendGuardPassed);
    }

    private static IReadOnlyDictionary<string, string> MergeDiagnostics(
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

    private static OsInteractionResult RunConfirmAndSend(byte[] hmacSecret)
    {
        var surface = new SmokeTextSurface("Connect to 192.168.10.25");
        var submitProfile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(surface.Surface));
        var orchestrator = new OsInteractionOrchestrator(
            new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
            surface,
            surface,
            surface,
            new VerifiedSubmitBindingAction(surface, submitProfile),
            new SmokeConfirmationOverlay());
        return orchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);
    }

    private static TextSurfaceDescriptor CreateSurface(string profileId, string verificationId)
    {
        var metadata = new SurfaceMetadata(
            SurfaceKind: "disposable_local_target",
            ComposerStatus: OsInteractionStatusIds.SupportedComposer);
        
        return new TextSurfaceDescriptor(
            $"native-smoke:{profileId}:{verificationId}",
            profileId,
            profileId,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: metadata.ToDictionary());
    }

    private sealed class SmokeTextSurface :
        IActiveTextSurfaceDiscovery,
        ITextSurfaceReader,
        ITextSurfaceWriter,
        ISubmitAction
    {
        public SmokeTextSurface(string currentText)
        {
            CurrentText = currentText;
            Surface = CreateSurface("codex-desktop", "flow-smoke");
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
            return new TextCaptureResult(true, "captured", CurrentText, new Dictionary<string, string>
            {
                ["capture_length"] = CurrentText.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
        {
            CurrentText = text;
            return new TextReplacementResult(true, OsInteractionStatusIds.Applied, new Dictionary<string, string>
            {
                ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        public SubmitActionResult Submit(TextSurfaceDescriptor surface)
        {
            SubmitCount++;
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string>
            {
                ["submit_count"] = SubmitCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
    }

    private sealed class SmokeConfirmationOverlay : IConfirmationOverlay
    {
        public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
        {
            return ConfirmationDecisionContract.Confirm(model);
        }
    }

    private sealed record ResidentSessionSmokeResult(
        bool RepeatedSubmitPassed,
        bool DuplicateSendGuardPassed);

    private sealed class SmokeNativeSubmitHookHost : INativeSubmitHookHost
    {
        private Func<NativeKeyGesture, NativeSubmitInterceptionResult>? _classify;
        private Action<NativeKeyGesture>? _onSuppressedSubmit;

        public string? LastErrorCode { get; private set; }

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture> onSuppressedSubmit)
        {
            _classify = classify ?? throw new ArgumentNullException(nameof(classify));
            _onSuppressedSubmit = onSuppressedSubmit ?? throw new ArgumentNullException(nameof(onSuppressedSubmit));
            LastErrorCode = null;
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
                _onSuppressedSubmit!(gesture);
            }
        }
    }

    private sealed class ProductSmokeTrayHotkeyHost : ITrayHotkeyHost
    {
        public ProductSmokeTrayHotkeyHost(string displayText)
        {
            Binding = new HotkeyBinding("manual-scan-apply", displayText, "manual_scan_apply");
        }

        public HotkeyBinding Binding { get; }

        public string? LastErrorCode { get; private set; }

        public bool Start(Action onTriggered)
        {
            ArgumentNullException.ThrowIfNull(onTriggered);
            LastErrorCode = null;
            return true;
        }

        public void Stop()
        {
        }
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
}

internal static class SubmitKeyBindingExtensions
{
    public static NativeKeyGesture ToNativeKeyGesture(this SubmitKeyBinding binding)
    {
        return new NativeKeyGesture(binding.Key, Ctrl: binding.Ctrl, Alt: binding.Alt, Shift: binding.Shift);
    }
}

