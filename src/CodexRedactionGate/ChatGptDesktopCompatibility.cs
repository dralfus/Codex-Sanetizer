using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace CodexRedactionGate;

internal static class ChatGptDesktopCompatibility
{
    private const string ProfileId = "chatgpt-desktop";

    private static readonly string[] RequiredEvidenceKeys =
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
            && profile.CompatibilityEvidence is null
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

        evidence = new SurfaceCompatibilityEvidence(
            discovery.Diagnostics["application_identity_hash"],
            discovery.Diagnostics["package_full_name_hash"],
            discovery.Diagnostics["application_version_hash"],
            discovery.Diagnostics["executable_name_hash"],
            discovery.Diagnostics["process_name_hash"],
            discovery.Diagnostics["window_class_hash"],
            discovery.Diagnostics["element_framework_id"],
            discovery.Diagnostics["element_control_type"],
            discovery.Diagnostics["composer_class_hash"],
            CreateVerificationId(discovery.Diagnostics, profile, activeSendEvidence ?? profile.Diagnostics),
            DateTimeOffset.UtcNow,
            profile.SubmitBinding.DisplayText,
            profile.NewlineBinding.DisplayText,
            FingerprintSendControl(activeSendEvidence ?? profile.Diagnostics));
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

    private static string FingerprintSendControl(IReadOnlyDictionary<string, string> diagnostics)
    {
        var automationId = diagnostics.TryGetValue(SendControlEvidence.AutomationIdHashKey, out var automationValue)
            ? automationValue : "not_available";
        var name = diagnostics.TryGetValue(SendControlEvidence.NameHashKey, out var nameValue)
            ? nameValue : "not_available";
        return Hash($"{automationId}|{name}");
    }

    private static string CreateVerificationId(
        IReadOnlyDictionary<string, string> diagnostics,
        SubmitBindingProfile profile,
        IReadOnlyDictionary<string, string> sendControlEvidence)
    {
        var evidence = string.Join("|", RequiredEvidenceKeys.Select(key => diagnostics[key]));
        var binding = $"submit={profile.SubmitBinding!.DisplayText}|newline={profile.NewlineBinding!.DisplayText}";
        var sendControl = $"send_control={FingerprintSendControl(sendControlEvidence)}";
        return Hash($"{evidence}|{binding}|{sendControl}");
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
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
