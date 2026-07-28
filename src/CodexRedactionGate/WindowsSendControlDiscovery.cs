using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Automation;

namespace CodexRedactionGate;

internal enum SendControlClassification
{
    Unrelated,
    NonSendControl,
    IdentifiedSend,
    SelectedClientUncertain
}

internal sealed record SendControlDiscoveryResult(
    SendControlClassification Classification,
    TextSurfaceDiscoveryResult ComposerDiscovery)
{
    public bool Identified => Classification == SendControlClassification.IdentifiedSend;
}

internal interface ISendControlDiscovery
{
    SendControlDiscoveryResult Discover(NativePointerGesture gesture);

    SendControlDiscoveryResult DiscoverFocusedControl();
}

internal static class SendControlEvidence
{
    internal const string AutomationIdHashKey = "send_control_automation_id_hash";
    internal const string NameHashKey = "send_control_name_hash";

    public static IReadOnlyDictionary<string, string> Create(string automationId, string name)
    {
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["send_control_evidence"] = "verified"
        };
        if (!string.IsNullOrWhiteSpace(automationId))
        {
            evidence[AutomationIdHashKey] = Hash(automationId);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            evidence[NameHashKey] = Hash(name);
        }

        return evidence;
    }

    public static bool Matches(IReadOnlyDictionary<string, string> diagnostics, string automationId, string name)
    {
        return (!string.IsNullOrWhiteSpace(automationId)
                && diagnostics.TryGetValue(AutomationIdHashKey, out var automationIdHash)
                && string.Equals(automationIdHash, Hash(automationId), StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(name)
                && diagnostics.TryGetValue(NameHashKey, out var nameHash)
                && string.Equals(nameHash, Hash(name), StringComparison.Ordinal));
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

internal sealed class WindowsSendControlDiscovery : ISendControlDiscovery
{
    private static readonly string[] SendTokens =
    {
        "send",
        "submit",
        "\u043e\u0442\u043f\u0440\u0430\u0432\u0438\u0442\u044c",
        "\u53d1\u9001",
        "\u53d1\u9001\u6d88\u606f"
    };
    private readonly SurfaceProfileCatalog _profiles;
    private readonly WindowsFocusedComposerDiscovery _composerDiscovery;
    private readonly DefaultStorageLayout? _storageLayout;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _persistedEvidence;

    public WindowsSendControlDiscovery(
        SurfaceProfileCatalog profiles,
        WindowsFocusedComposerDiscovery composerDiscovery,
        DefaultStorageLayout? storageLayout = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _composerDiscovery = composerDiscovery ?? throw new ArgumentNullException(nameof(composerDiscovery));
        _storageLayout = storageLayout;
        _persistedEvidence = storageLayout is null
            ? new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            : new ConcurrentDictionary<string, IReadOnlyDictionary<string, string>>(
                SubmitBindingProfileStore.Load(storageLayout).Profiles.ToDictionary(
                profile => profile.ProfileId,
                profile => profile.Diagnostics,
                StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    public static WindowsSendControlDiscovery CreateDefault(DefaultStorageLayout? storageLayout = null)
    {
        return new WindowsSendControlDiscovery(
            SurfaceProfileCatalog.Default,
            WindowsFocusedComposerDiscovery.CreateDefault(),
            storageLayout);
    }

    public SendControlDiscoveryResult Discover(NativePointerGesture gesture)
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(gesture.Button, "left", StringComparison.Ordinal))
        {
            return Unrelated();
        }

        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(gesture.X, gesture.Y));
            if (element is null)
            {
                return Unrelated();
            }

            return DiscoverElement(element, gesture.TargetWindow, classifyPotentialComposerAsUncertain: false);
        }
        catch (ElementNotAvailableException)
        {
            return SelectedClientUncertain(gesture.TargetWindow);
        }
        catch (InvalidOperationException)
        {
            return SelectedClientUncertain(gesture.TargetWindow);
        }
        catch (COMException)
        {
            return SelectedClientUncertain(gesture.TargetWindow);
        }
        catch (Exception)
        {
            return SelectedClientUncertain(gesture.TargetWindow);
        }
    }

    public SendControlDiscoveryResult DiscoverFocusedControl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Unrelated();
        }

        var targetWindow = NativeMethods.GetForegroundWindow();
        try
        {
            var element = AutomationElement.FocusedElement;
            return element is null
                ? SelectedClientUncertain(targetWindow)
                : DiscoverElement(element, targetWindow, classifyPotentialComposerAsUncertain: true);
        }
        catch (ElementNotAvailableException)
        {
            return SelectedClientUncertain(targetWindow);
        }
        catch (InvalidOperationException)
        {
            return SelectedClientUncertain(targetWindow);
        }
        catch (COMException)
        {
            return SelectedClientUncertain(targetWindow);
        }
        catch (Exception)
        {
            return SelectedClientUncertain(targetWindow);
        }
    }

    private SendControlDiscoveryResult DiscoverElement(
        AutomationElement element,
        IntPtr fallbackWindow,
        bool classifyPotentialComposerAsUncertain)
    {
        var window = FindOwningWindow(element);
        if (window == IntPtr.Zero)
        {
            window = RootWindow(fallbackWindow);
        }
        if (window == IntPtr.Zero)
        {
            return Unrelated();
        }

        var match = _profiles.Match(GetWindowText(window), GetProcessName(window));
        if (!match.Matched || match.Profile is null)
        {
            return Unrelated();
        }

        if (element.Current.ControlType != ControlType.Button)
        {
            if (classifyPotentialComposerAsUncertain && IsPotentialComposer(element))
            {
                return SelectedClientUncertain(window);
            }

            return NonSendControl(match.Profile.ProfileId);
        }

        if (!IsSendControl(match.Profile, element))
        {
            return NonSendControl(match.Profile.ProfileId);
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

        return new SendControlDiscoveryResult(SendControlClassification.IdentifiedSend, composer);
    }

    private static bool IsPotentialComposer(AutomationElement element)
    {
        var controlType = element.Current.ControlType;
        return controlType.Equals(ControlType.Edit)
            || controlType.Equals(ControlType.Document)
            || controlType.Equals(ControlType.Text)
            || element.TryGetCurrentPattern(TextPattern.Pattern, out _)
            || element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
    }

    private static SendControlDiscoveryResult Unrelated()
    {
        return new SendControlDiscoveryResult(
            SendControlClassification.Unrelated,
            TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer, new Dictionary<string, string>()));
    }

    private static SendControlDiscoveryResult NonSendControl(string profileId)
    {
        return new SendControlDiscoveryResult(
            SendControlClassification.NonSendControl,
            TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.NotComposer,
                new Dictionary<string, string> { ["profile_id"] = profileId }));
    }

    internal static string? TryGetSelectedProfileId(IntPtr targetWindow)
    {
        try
        {
            var window = RootWindow(targetWindow);
            if (window == IntPtr.Zero)
            {
                return null;
            }

            var match = SurfaceProfileCatalog.Default.Match(GetWindowText(window), GetProcessName(window));
            return match.Matched && match.Profile is not null ? match.Profile.ProfileId : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static SendControlDiscoveryResult SelectedClientUncertain(IntPtr targetWindow)
    {
        var profileId = TryGetSelectedProfileId(targetWindow);
        return string.IsNullOrWhiteSpace(profileId)
            ? Unrelated()
            : new SendControlDiscoveryResult(
                SendControlClassification.SelectedClientUncertain,
                TextSurfaceDiscoveryResult.Failure(
                    OsInteractionStatusIds.SurfaceUnverified,
                    new Dictionary<string, string> { ["profile_id"] = profileId }));
    }

    private bool IsSendControl(SurfaceProfile profile, AutomationElement element)
    {
        var name = element.Current.Name ?? string.Empty;
        var automationId = element.Current.AutomationId ?? string.Empty;
        if (_persistedEvidence.TryGetValue(profile.ProfileId, out var evidence)
            && SendControlEvidence.Matches(evidence, automationId, name))
        {
            return true;
        }

        if (!ContainsKnownToken(name) && !ContainsKnownToken(automationId))
        {
            return false;
        }

        PersistEvidence(profile.ProfileId, automationId, name);
        return true;
    }

    private void PersistEvidence(string profileId, string automationId, string name)
    {
        var evidence = SendControlEvidence.Create(automationId, name);
        _persistedEvidence[profileId] = evidence;
        if (_storageLayout is null)
        {
            return;
        }

        SubmitBindingProfileStore.MergeDiagnostics(_storageLayout, profileId, evidence);
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
                return RootWindow(handle);
            }

            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return IntPtr.Zero;
    }

    private static IntPtr RootWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var root = NativeMethods.GetAncestor(handle, 2);
        return root == IntPtr.Zero ? handle : root;
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

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder buffer, int maximumCount);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    }
}
