using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

internal static class ChatGptDesktopCompatibility
{
    private const string ProfileId = "chatgpt-desktop";

    internal static IReadOnlyList<string> RequiredEvidenceKeys { get; } = new[]
    {
        "application_identity_hash",
        "application_version_hash",
        "application_version_status",
        "package_full_name_hash",
        "executable_name_hash",
        "process_name_hash",
        "window_class_hash",
        "composer_class_hash",
        "window_identity_hash",
        "element_control_type",
        "element_framework_id",
        "focused_element_hash"
    };

    public static SubmitBindingProfile RequirePinnedFingerprint(SubmitBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Equals(profile.ProfileId, ProfileId, StringComparison.Ordinal)
            && profile.IsProtected
            && (profile.CompatibilityEvidence is null || !profile.CompatibilityEvidence.IsComplete)
            ? profile with
            {
                Enabled = false,
                CapabilityStatus = OsInteractionStatusIds.SurfaceUnverified,
                Diagnostics = Merge(profile.Diagnostics, ("compatibility", "fingerprint_missing"))
            }
            : profile;
    }

    public static bool TryCreate(
        SubmitBindingProfile profile,
        TextSurfaceDiscoveryResult discovery,
        IReadOnlyDictionary<string, string>? activeSendEvidence,
        out SurfaceCompatibilityEvidence? evidence)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(discovery);
        evidence = null;
        if (!string.Equals(profile.ProfileId, ProfileId, StringComparison.Ordinal)
            || !discovery.Succeeded
            || discovery.Surface is null
            || profile.SubmitBinding is null
            || profile.NewlineBinding is null
            || !discovery.Diagnostics.TryGetValue("application_version_status", out var versionStatus)
            || !string.Equals(versionStatus, "available", StringComparison.Ordinal)
            || RequiredEvidenceKeys.Any(key => !discovery.Diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)))
        {
            return false;
        }

        var fingerprints = new Dictionary<string, OpaqueFingerprint>(StringComparer.Ordinal);
        foreach (var key in RequiredEvidenceKeys)
        {
            if (key == "application_version_status")
            {
                continue;
            }

            if (!TryReadFingerprint(discovery.Diagnostics, key, out var fingerprint))
            {
                return false;
            }

            fingerprints[key] = fingerprint;
        }

        var sendControlEvidence = activeSendEvidence ?? profile.Diagnostics;
        if (!TryReadSendControlFingerprint(sendControlEvidence, out var sendControlFingerprint))
        {
            return false;
        }

        var activeVersionStatus = discovery.Diagnostics["application_version_status"];
        var verificationFingerprint = CreateVerificationId(
            discovery.Diagnostics,
            profile,
            fingerprints,
            sendControlFingerprint);
        evidence = new SurfaceCompatibilityEvidence(
            fingerprints["application_identity_hash"],
            fingerprints["application_version_hash"],
            activeVersionStatus,
            fingerprints["package_full_name_hash"],
            fingerprints["executable_name_hash"],
            fingerprints["process_name_hash"],
            fingerprints["window_identity_hash"],
            fingerprints["window_class_hash"],
            fingerprints["element_framework_id"],
            fingerprints["element_control_type"],
            fingerprints["composer_class_hash"],
            fingerprints["focused_element_hash"],
            verificationFingerprint,
            DateTimeOffset.UtcNow,
            profile.SubmitBinding.DisplayText,
            profile.NewlineBinding.DisplayText,
            sendControlFingerprint);
        return true;
    }

    public static IReadOnlyDictionary<string, string>? ActiveEvidence(
        SubmitBindingProfile profile,
        TextSurfaceDiscoveryResult discovery)
    {
        return TryCreate(profile, discovery, discovery.Diagnostics, out var evidence) && evidence is not null
            ? evidence.ToComparisonDiagnostics()
            : null;
    }

    private static bool TryReadFingerprint(
        IReadOnlyDictionary<string, string> diagnostics,
        string key,
        out OpaqueFingerprint fingerprint)
    {
        if (!diagnostics.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            fingerprint = default;
            return false;
        }

        if (key is "element_control_type" or "element_framework_id")
        {
            fingerprint = OpaqueFingerprint.FromSource(value);
            return true;
        }

        return OpaqueFingerprint.TryParse(value, out fingerprint);
    }

    private static bool TryReadSendControlFingerprint(
        IReadOnlyDictionary<string, string> diagnostics,
        out OpaqueFingerprint fingerprint)
    {
        var hasAutomationId = diagnostics.TryGetValue(
            SendControlEvidence.AutomationIdHashKey,
            out var automationValue);
        var hasName = diagnostics.TryGetValue(SendControlEvidence.NameHashKey, out var nameValue);

        if (!hasAutomationId && !hasName)
        {
            fingerprint = OpaqueFingerprint.FromSource("send_control_not_available");
            return true;
        }

        if (!OpaqueFingerprint.TryParse(automationValue, out var automationId)
            || !OpaqueFingerprint.TryParse(nameValue, out var name))
        {
            fingerprint = default;
            return false;
        }

        fingerprint = OpaqueFingerprint.FromSource($"{automationId.Value}|{name.Value}");
        return true;
    }

    private static OpaqueFingerprint CreateVerificationId(
        IReadOnlyDictionary<string, string> diagnostics,
        SubmitBindingProfile profile,
        IReadOnlyDictionary<string, OpaqueFingerprint> fingerprints,
        OpaqueFingerprint sendControlFingerprint)
    {
        var evidence = string.Join(
            "|",
            RequiredEvidenceKeys.Select(key => key == "application_version_status"
                ? diagnostics[key]
                : fingerprints[key].Value));
        var binding = $"submit={profile.SubmitBinding!.DisplayText}|newline={profile.NewlineBinding!.DisplayText}";
        var sendControl = $"send_control={sendControlFingerprint.Value}";
        return OpaqueFingerprint.FromSource($"{evidence}|{binding}|{sendControl}");
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> diagnostics,
        params (string Key, string Value)[] values)
    {
        var merged = new Dictionary<string, string>(diagnostics, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            merged[key] = value;
        }

        return merged;
    }
}
