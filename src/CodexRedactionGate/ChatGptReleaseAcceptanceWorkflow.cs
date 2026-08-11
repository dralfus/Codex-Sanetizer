using System;
using System.Linq;
using System.Text;

namespace CodexRedactionGate;

internal sealed record ChatGptReleaseAcceptanceResult(bool Succeeded, string Code);

/// <summary>
/// Runs the local half of the pinned ChatGPT release acceptance and arms exactly
/// one subsequent live-contract observation for the current profile/build.
/// </summary>
internal static class ChatGptReleaseAcceptanceWorkflow
{
    private static readonly byte[] ReferenceAcceptanceSecret =
        Encoding.UTF8.GetBytes("reference-composer-release-acceptance-secret");

    internal static ChatGptReleaseAcceptanceResult RunAndArm(
        DefaultStorageLayout layout,
        Func<bool>? interactiveDesktopProbe = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var profile = SubmitBindingProfileStore.Load(layout).Profiles
            .FirstOrDefault(item => string.Equals(item.ProfileId, "chatgpt-desktop", StringComparison.Ordinal));
        if (profile is null || !profile.IsProtected)
        {
            return new ChatGptReleaseAcceptanceResult(false, "chatgpt_profile_unavailable");
        }

        var report = ReferenceComposerReleaseAcceptanceRunner.Run(
            ReferenceAcceptanceSecret,
            interactiveDesktopProbe);
        if (!report.Passed)
        {
            return new ChatGptReleaseAcceptanceResult(false, "reference_acceptance_failed");
        }

        if (!ChatGptAcceptanceProofStore.RecordReference(
                layout,
                profile,
                BuildVersion.Current,
                passed: true,
                terminalStatus: "passed"))
        {
            return new ChatGptReleaseAcceptanceResult(false, "reference_proof_not_recorded");
        }

        return ChatGptAcceptanceProofStore.ArmLiveContract(layout, profile)
            ? new ChatGptReleaseAcceptanceResult(true, "live_contract_armed")
            : new ChatGptReleaseAcceptanceResult(false, "live_contract_arm_failed");
    }
}
