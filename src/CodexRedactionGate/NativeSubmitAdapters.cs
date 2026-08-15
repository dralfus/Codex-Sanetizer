using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

/// <summary>
/// Storage and verification boundary for submit-binding profiles. Native input
/// callbacks never receive this adapter and therefore cannot read local state.
/// </summary>
internal interface ISubmitBindingProfileAdapter
{
    SubmitBindingProfileStoreResult Load(DefaultStorageLayout layout);

    SubmitBindingProfileStoreResult Save(DefaultStorageLayout layout, IReadOnlyList<SubmitBindingProfile> profiles);

    SubmitBindingProfileStoreResult Upsert(DefaultStorageLayout layout, SubmitBindingProfile profile);
}

internal sealed class LocalSubmitBindingProfileAdapter : ISubmitBindingProfileAdapter
{
    public static LocalSubmitBindingProfileAdapter Instance { get; } = new();

    private LocalSubmitBindingProfileAdapter()
    {
    }

    public SubmitBindingProfileStoreResult Load(DefaultStorageLayout layout) => SubmitBindingProfileStore.Load(layout);

    public SubmitBindingProfileStoreResult Save(DefaultStorageLayout layout, IReadOnlyList<SubmitBindingProfile> profiles) =>
        SubmitBindingProfileStore.Save(layout, profiles);

    public SubmitBindingProfileStoreResult Upsert(DefaultStorageLayout layout, SubmitBindingProfile profile) =>
        SubmitBindingProfileStore.Upsert(layout, profile);
}

public sealed record NativeSubmitProfileSnapshot(
    string ProfileId,
    string SetupStatus,
    SubmitKeyBinding? PendingSubmitBinding,
    bool LiveContractCaptureArmed = false)
{
    public static NativeSubmitProfileSnapshot FromProfile(SubmitBindingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var status = profile.IsSetupComplete
            ? OsInteractionStatusIds.Protected
            : OsInteractionStatusIds.NativeSubmitSetupRequired;
        return new NativeSubmitProfileSnapshot(profile.ProfileId, status, profile.SubmitBinding);
    }
}

internal static class NativeSubmitProfileSnapshotAdapter
{
    public static NativeSubmitProfileSnapshot Load(
        ISubmitBindingProfileAdapter profiles,
        DefaultStorageLayout layout,
        SubmitBindingProfile activeProfile)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(activeProfile);

        return FromLoadResult(profiles.Load(layout), activeProfile, layout);
    }

    public static NativeSubmitProfileSnapshot FromLoadResult(
        SubmitBindingProfileStoreResult loaded,
        SubmitBindingProfile activeProfile,
        DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(activeProfile);
        ArgumentNullException.ThrowIfNull(layout);

        if (!loaded.Succeeded)
        {
            return new NativeSubmitProfileSnapshot(
                activeProfile.ProfileId,
                OsInteractionStatusIds.ProfilesUnavailable,
                activeProfile.SubmitBinding);
        }

        var stored = loaded.Profiles.FirstOrDefault(profile => string.Equals(
            profile.ProfileId,
            activeProfile.ProfileId,
            StringComparison.Ordinal));
        if (stored is null)
        {
            return new NativeSubmitProfileSnapshot(
                activeProfile.ProfileId,
                OsInteractionStatusIds.ProfilesUnavailable,
                activeProfile.SubmitBinding);
        }

        var profile = stored;
        var snapshot = NativeSubmitProfileSnapshot.FromProfile(profile);
        return snapshot with
        {
            LiveContractCaptureArmed = IsLiveContractCaptureArmed(profile, layout)
        };
    }

    private static bool IsLiveContractCaptureArmed(
        SubmitBindingProfile profile,
        DefaultStorageLayout layout)
    {
        return string.Equals(profile.ProfileId, "chatgpt-desktop", StringComparison.Ordinal)
            && profile.IsProtected
            && ChatGptAcceptanceProofStore.IsLiveContractArmed(
                layout,
                profile,
                BuildVersion.Current);
    }
}

/// <summary>
/// Bounded native-input boundary. It receives already constructed callbacks and
/// has no dependency on profile storage, UIA discovery, or setup workflows.
/// </summary>
internal interface INativeSubmitInputAdapter
{
    bool Start(
        INativeSubmitHookHost hookHost,
        Func<NativeKeyGesture, NativeSubmitInterceptionResult> classifyKeyboard,
        Action<NativeKeyGesture, NativeSubmitInterceptionResult> onSuppressedKeyboardSubmit,
        Func<NativeKeyGesture, bool> shouldSuppressKeyboardFailure,
        Func<NativePointerGesture, NativeSubmitInterceptionResult>? classifyPointer,
        Action<NativePointerGesture, NativeSubmitInterceptionResult>? onSuppressedPointerSubmit,
        Func<NativePointerGesture, bool>? shouldSuppressPointerFailure);
}

internal sealed class NativeSubmitInputAdapter : INativeSubmitInputAdapter
{
    public static NativeSubmitInputAdapter Instance { get; } = new();

    private NativeSubmitInputAdapter()
    {
    }

    public bool Start(
        INativeSubmitHookHost hookHost,
        Func<NativeKeyGesture, NativeSubmitInterceptionResult> classifyKeyboard,
        Action<NativeKeyGesture, NativeSubmitInterceptionResult> onSuppressedKeyboardSubmit,
        Func<NativeKeyGesture, bool> shouldSuppressKeyboardFailure,
        Func<NativePointerGesture, NativeSubmitInterceptionResult>? classifyPointer,
        Action<NativePointerGesture, NativeSubmitInterceptionResult>? onSuppressedPointerSubmit,
        Func<NativePointerGesture, bool>? shouldSuppressPointerFailure)
    {
        ArgumentNullException.ThrowIfNull(hookHost);
        ArgumentNullException.ThrowIfNull(classifyKeyboard);
        ArgumentNullException.ThrowIfNull(onSuppressedKeyboardSubmit);
        ArgumentNullException.ThrowIfNull(shouldSuppressKeyboardFailure);

        if (!hookHost.Start(classifyKeyboard, onSuppressedKeyboardSubmit, shouldSuppressKeyboardFailure))
        {
            return false;
        }

        if (classifyPointer is null || onSuppressedPointerSubmit is null || shouldSuppressPointerFailure is null)
        {
            return true;
        }

        if (hookHost is not INativeSubmitPointerHookHost pointerHook
            || pointerHook.StartPointer(classifyPointer, onSuppressedPointerSubmit, shouldSuppressPointerFailure))
        {
            return true;
        }

        hookHost.Stop();
        return false;
    }
}
