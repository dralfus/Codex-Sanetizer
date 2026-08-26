using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CodexRedactionGate;

/// <summary>
/// Raw-free identity captured at interception time. Deferred work may act only
/// on the same selected profile and native window.
/// </summary>
internal sealed record NativeSubmitTargetIdentity(
    long SnapshotGeneration,
    string ProfileId,
    string WindowHandle,
    TextSurfaceDescriptor? CapturedSurface = null)
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

        return new NativeSubmitTargetIdentity(snapshotGeneration, surface.ProfileId, windowHandle, surface);
    }

    public static NativeSubmitTargetIdentity? TryCreateForGesture(
        long snapshotGeneration,
        TextSurfaceDescriptor? surface,
        IntPtr gestureTargetWindow,
        Func<IntPtr, IntPtr>? rootWindowResolver = null)
    {
        var target = TryCreate(snapshotGeneration, surface);
        var normalizedGestureWindow = (rootWindowResolver ?? NormalizeGestureTargetWindow)(gestureTargetWindow);
        return target is not null
            && normalizedGestureWindow != IntPtr.Zero
            && string.Equals(
                target.WindowHandle,
                normalizedGestureWindow.ToInt64().ToString("X"),
                StringComparison.Ordinal)
            ? target
            : null;
    }

    private static IntPtr NormalizeGestureTargetWindow(IntPtr gestureTargetWindow)
    {
        if (gestureTargetWindow == IntPtr.Zero || !OperatingSystem.IsWindows())
        {
            return gestureTargetWindow;
        }

        try
        {
            var root = NativeMethods.GetAncestor(gestureTargetWindow, NativeMethods.GaRoot);
            return root == IntPtr.Zero ? gestureTargetWindow : root;
        }
        catch
        {
            return gestureTargetWindow;
        }
    }

    private static class NativeMethods
    {
        internal const uint GaRoot = 2;

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    }
}

internal sealed class CapturedTargetSurfaceDiscovery : IActiveTextSurfaceDiscovery
{
    private readonly IActiveTextSurfaceDiscovery _inner;
    private readonly NativeSubmitTargetIdentity _target;
    private readonly Func<string, bool> _isTargetWindowForeground;

    public CapturedTargetSurfaceDiscovery(
        IActiveTextSurfaceDiscovery inner,
        NativeSubmitTargetIdentity target,
        Func<string, bool>? isTargetWindowForeground = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _isTargetWindowForeground = isTargetWindowForeground ?? IsTargetWindowForeground;
    }

    public TextSurfaceDiscoveryResult DiscoverActiveSurface()
    {
        var discovery = _inner.DiscoverActiveSurface();
        if (!discovery.Succeeded || discovery.Surface is null || !discovery.Surface.Supported)
        {
            if (_target.CapturedSurface is not null
                && IsRecoverableFocusFailure(discovery.Status)
                && _isTargetWindowForeground(_target.WindowHandle))
            {
                return TextSurfaceDiscoveryResult.Success(
                    _target.CapturedSurface,
                    Merge(
                        Merge(discovery.Diagnostics, ("target_identity", "anchored_after_overlay")),
                        ("focus_status", discovery.Status)));
            }

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

    private static bool IsRecoverableFocusFailure(string status)
    {
        return status is OsInteractionStatusIds.FocusLost
            or OsInteractionStatusIds.NotComposer
            or OsInteractionStatusIds.UnsupportedSurface;
    }

    private static bool IsTargetWindowForeground(string windowHandle)
    {
        if (!OperatingSystem.IsWindows()
            || !IntPtr.TryParse(windowHandle, System.Globalization.NumberStyles.HexNumber, null, out var expectedWindow)
            || expectedWindow == IntPtr.Zero)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var root = foreground == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot);
        return expectedWindow == (root == IntPtr.Zero ? foreground : root);
    }

    private static class NativeMethods
    {
        internal const uint GaRoot = 2;

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
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
