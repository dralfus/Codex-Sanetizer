using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

internal static class ChatGptDesktopCompatibility
{
    internal static IReadOnlyList<string> RequiredEvidenceKeys => OpenAiDesktopIdentity.RequiredEvidenceKeys;

    internal static bool IsTransientTargetDiagnosticKey(string key)
    {
        var normalized = key.StartsWith("surface.", StringComparison.Ordinal)
            ? key["surface.".Length..]
            : key;
        return normalized is "target_process_hash" or "window_identity_hash" or "focused_element_hash";
    }

    internal static IReadOnlyDictionary<string, string> PersistedDiscoveryDiagnostics(
        IReadOnlyDictionary<string, string> discoveryDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(discoveryDiagnostics);
        var persisted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in discoveryDiagnostics)
        {
            if (!IsTransientTargetDiagnosticKey(item.Key))
            {
                persisted[item.Key] = item.Value;
            }
        }

        return persisted;
    }

    public static SubmitBindingProfile RequirePinnedFingerprint(SubmitBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return OpenAiDesktopIdentity.IsSupportedProfileId(profile.ProfileId)
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
        if (!OpenAiDesktopIdentity.IsSupportedProfileId(profile.ProfileId)
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
