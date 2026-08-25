using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

internal static class ChatGptDesktopCompatibility
{
    private const string ProfileId = "chatgpt-desktop";

    internal static IReadOnlyList<string> RequiredEvidenceKeys => OpenAiDesktopIdentity.RequiredEvidenceKeys;

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
            || !OpenAiDesktopIdentity.TryCreate(discovery.Diagnostics, out var desktopIdentity)
            || desktopIdentity is null)
        {
            return false;
        }

        var sendControlEvidence = activeSendEvidence ?? profile.Diagnostics;
        if (!TryReadSendControlFingerprint(sendControlEvidence, out var sendControlFingerprint))
        {
            return false;
        }

        var verificationFingerprint = CreateVerificationId(
            desktopIdentity,
            profile,
            sendControlFingerprint);
        evidence = new SurfaceCompatibilityEvidence(
            desktopIdentity.ApplicationIdentityFingerprint,
            desktopIdentity.ApplicationVersionFingerprint,
            desktopIdentity.ApplicationVersionStatus,
            desktopIdentity.PackageFullNameFingerprint,
            desktopIdentity.ExecutableNameFingerprint,
            desktopIdentity.ProcessNameFingerprint,
            default,
            desktopIdentity.WindowClassFingerprint,
            desktopIdentity.FrameworkFingerprint,
            desktopIdentity.ControlTypeFingerprint,
            desktopIdentity.ComposerClassFingerprint,
            default,
            verificationFingerprint,
            DateTimeOffset.UtcNow,
            profile.SubmitBinding.DisplayText,
            profile.NewlineBinding.DisplayText,
            sendControlFingerprint)
        {
            DesktopIdentity = desktopIdentity,
            VerifiedTargetFingerprint = TransientTargetFingerprint.TryCreate(discovery.Diagnostics)
        };
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
        OpenAiDesktopIdentity desktopIdentity,
        SubmitBindingProfile profile,
        OpaqueFingerprint sendControlFingerprint)
    {
        var evidence = string.Join("|", desktopIdentity.ToComparisonDiagnostics()
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}"));
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
