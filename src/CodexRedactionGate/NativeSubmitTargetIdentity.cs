using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

/// <summary>
/// Raw-free identity captured at interception time. Deferred work may act only
/// on the same selected profile and native window.
/// </summary>
internal sealed record NativeSubmitTargetIdentity(
    long SnapshotGeneration,
    string ProfileId,
    string WindowHandle)
{
    public static NativeSubmitTargetIdentity? TryCreate(long snapshotGeneration, TextSurfaceDescriptor? surface)
    {
        if (surface is null
            || string.IsNullOrWhiteSpace(surface.ProfileId)
            || !surface.Metadata.TryGetValue("window_handle", out var windowHandle)
            || string.IsNullOrWhiteSpace(windowHandle))
        {
            return null;
        }

        return new NativeSubmitTargetIdentity(snapshotGeneration, surface.ProfileId, windowHandle);
    }

    public static NativeSubmitTargetIdentity? TryCreateForGesture(
        long snapshotGeneration,
        TextSurfaceDescriptor? surface,
        IntPtr gestureTargetWindow)
    {
        var target = TryCreate(snapshotGeneration, surface);
        return target is not null
            && gestureTargetWindow != IntPtr.Zero
            && string.Equals(
                target.WindowHandle,
                gestureTargetWindow.ToInt64().ToString("X"),
                StringComparison.Ordinal)
            ? target
            : null;
    }
}

internal sealed class CapturedTargetSurfaceDiscovery : IActiveTextSurfaceDiscovery
{
    private readonly IActiveTextSurfaceDiscovery _inner;
    private readonly NativeSubmitTargetIdentity _target;

    public CapturedTargetSurfaceDiscovery(IActiveTextSurfaceDiscovery inner, NativeSubmitTargetIdentity target)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public TextSurfaceDiscoveryResult DiscoverActiveSurface()
    {
        var discovery = _inner.DiscoverActiveSurface();
        if (!discovery.Succeeded || discovery.Surface is null || !discovery.Surface.Supported)
        {
            return TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.FocusLost,
                Merge(discovery.Diagnostics, ("target_identity", "unavailable")));
        }

        if (!string.Equals(discovery.Surface.ProfileId, _target.ProfileId, StringComparison.Ordinal)
            || !discovery.Surface.Metadata.TryGetValue("window_handle", out var windowHandle)
            || !string.Equals(windowHandle, _target.WindowHandle, StringComparison.Ordinal))
        {
            return TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.StaleComposer,
                Merge(discovery.Diagnostics, ("target_identity", "changed")));
        }

        return discovery;
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> diagnostics,
        (string Key, string Value) value)
    {
        var merged = new Dictionary<string, string>(diagnostics, StringComparer.Ordinal)
        {
            [value.Key] = value.Value
        };
        return merged;
    }
}
