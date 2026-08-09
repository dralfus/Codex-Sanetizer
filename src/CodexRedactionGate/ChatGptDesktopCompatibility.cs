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
            discovery.Diagnostics["application_version_hash"],
            discovery.Diagnostics["application_version_hash"],
            discovery.Diagnostics["application_identity_hash"],
            discovery.Diagnostics["application_identity_hash"],
            discovery.Diagnostics["window_identity_hash"],
            discovery.Diagnostics["element_framework_id"],
            discovery.Diagnostics["element_control_type"],
            discovery.Diagnostics["focused_element_hash"],
            CreateVerificationId(discovery.Diagnostics),
            DateTimeOffset.UtcNow,
            profile.SubmitBinding.DisplayText,
            profile.NewlineBinding.DisplayText,
            FingerprintSendControl(profile.Diagnostics));
        return true;
    }

    public static IReadOnlyDictionary<string, string>? ActiveEvidence(
        SubmitBindingProfile profile,
        TextSurfaceDiscoveryResult discovery)
    {
        return TryCreate(profile, discovery, out var evidence) && evidence is not null
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

    private static string CreateVerificationId(IReadOnlyDictionary<string, string> diagnostics)
    {
        return Hash(string.Join("|", RequiredEvidenceKeys.Select(key => diagnostics[key])));
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
