using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CodexRedactionGate;

internal sealed record SendControlDiscoveryResult(
    bool Identified,
    TextSurfaceDiscoveryResult ComposerDiscovery);

internal interface ISendControlDiscovery
{
    SendControlDiscoveryResult Discover(NativePointerGesture gesture);
}

internal sealed class WindowsSendControlDiscovery : ISendControlDiscovery
{
    private static readonly string[] SendTokens = { "send", "submit" };
    private readonly SurfaceProfileCatalog _profiles;
    private readonly WindowsFocusedComposerDiscovery _composerDiscovery;

    public WindowsSendControlDiscovery(
        SurfaceProfileCatalog profiles,
        WindowsFocusedComposerDiscovery composerDiscovery)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _composerDiscovery = composerDiscovery ?? throw new ArgumentNullException(nameof(composerDiscovery));
    }

    public static WindowsSendControlDiscovery CreateDefault()
    {
        return new WindowsSendControlDiscovery(
            SurfaceProfileCatalog.Default,
            WindowsFocusedComposerDiscovery.CreateDefault());
    }

    public SendControlDiscoveryResult Discover(NativePointerGesture gesture)
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(gesture.Button, "left", StringComparison.Ordinal))
        {
            return NotIdentified();
        }

        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(gesture.X, gesture.Y));
            if (element is null || element.Current.ControlType != ControlType.Button || !IsSendControl(element))
            {
                return NotIdentified();
            }

            var window = FindOwningWindow(element);
            if (window == IntPtr.Zero)
            {
                return NotIdentified();
            }

            var match = _profiles.Match(GetWindowText(window), GetProcessName(window));
            if (!match.Matched || match.Profile is null)
            {
                return NotIdentified();
            }

            var composer = _composerDiscovery.DiscoverActiveSurface();
            if (composer.Surface is not null
                && !string.Equals(composer.Surface.ProfileId, match.Profile.ProfileId, StringComparison.Ordinal))
            {
                composer = TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.SurfaceUnverified,
                    new Dictionary<string, string>
                    {
                        ["send_control_profile_match"] = "false"
                    });
            }

            return new SendControlDiscoveryResult(true, composer);
        }
        catch (ElementNotAvailableException)
        {
            return NotIdentified();
        }
        catch (InvalidOperationException)
        {
            return NotIdentified();
        }
        catch (COMException)
        {
            return NotIdentified();
        }
    }

    private static SendControlDiscoveryResult NotIdentified()
    {
        return new SendControlDiscoveryResult(
            false,
            TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()));
    }

    private static bool IsSendControl(AutomationElement element)
    {
        var name = element.Current.Name ?? string.Empty;
        var automationId = element.Current.AutomationId ?? string.Empty;
        return ContainsKnownToken(name) || ContainsKnownToken(automationId);
    }

    private static bool ContainsKnownToken(string value)
    {
        foreach (var token in SendTokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IntPtr FindOwningWindow(AutomationElement element)
    {
        var current = element;
        while (current is not null)
        {
            var handle = new IntPtr(current.Current.NativeWindowHandle);
            if (handle != IntPtr.Zero)
            {
                return NativeMethods.GetAncestor(handle, 2);
            }

            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return IntPtr.Zero;
    }

    private static string GetWindowText(IntPtr window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(length + 1);
        return NativeMethods.GetWindowText(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    private static string GetProcessName(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr handle, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder buffer, int maximumCount);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    }
}
