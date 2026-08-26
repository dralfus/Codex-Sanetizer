using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexRedactionGate;

public sealed record FocusedElementSnapshot(
    bool Succeeded,
    string Status,
    string WindowTitle,
    string ProcessName,
    string WindowClassName,
    IntPtr WindowHandle,
    string ElementControlType,
    string ElementClassName,
    string ElementAutomationId,
    string ElementFrameworkId,
    bool HasKeyboardFocus,
    bool IsKeyboardFocusable,
    bool IsEnabled,
    bool IsPassword,
    bool CanReadValue,
    bool CanWriteValue,
    bool IsValueReadOnly,
    bool CanReadTextPattern,
    bool CanUseKeyboardTextInput,
    string ElementRuntimeIdHash,
    string FocusResolutionStage = "unknown",
    string PackageFullName = "",
    string ExecutableName = "",
    string ApplicationVersion = "");

public interface IFocusedElementSnapshotProvider
{
    FocusedElementSnapshot GetFocusedElement();
}

public interface IVerifiedComposerTextAccess
{
    TextCaptureResult CaptureText(TextSurfaceDescriptor surface);

    TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text);

    SubmitActionResult Submit(TextSurfaceDescriptor surface);
}

public sealed class WindowsFocusedComposerDiscovery : IActiveTextSurfaceDiscovery
{
    private readonly SurfaceProfileCatalog _profiles;
    private readonly IFocusedElementSnapshotProvider _snapshotProvider;

    public WindowsFocusedComposerDiscovery(
        SurfaceProfileCatalog profiles,
        IFocusedElementSnapshotProvider snapshotProvider)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
    }

    public static WindowsFocusedComposerDiscovery CreateDefault()
    {
        return new WindowsFocusedComposerDiscovery(
            SurfaceProfileCatalog.Default,
            OperatingSystem.IsWindows()
                ? new NativeFocusedElementSnapshotProvider()
                : new UnsupportedFocusedElementSnapshotProvider());
    }

    public TextSurfaceDiscoveryResult DiscoverActiveSurface()
    {
        var snapshot = _snapshotProvider.GetFocusedElement();
        if (!snapshot.Succeeded)
        {
            return TextSurfaceDiscoveryResult.Failure(
                snapshot.Status,
                Diagnostics(snapshot));
        }

        var match = _profiles.Match(snapshot.WindowTitle, snapshot.ProcessName);
        if (!match.Matched || match.Profile is null)
        {
            return TextSurfaceDiscoveryResult.Failure(
                match.Status,
                Merge(match.Diagnostics, Diagnostics(snapshot)));
        }

        var composer = ClassifyComposer(snapshot, match.Profile);
        var composerStatus = composer.Status;
        if (composerStatus != OsInteractionStatusIds.SupportedComposer)
        {
            return TextSurfaceDiscoveryResult.Failure(
                composerStatus,
                Merge(
                    Merge(match.Diagnostics, Diagnostics(snapshot)),
                    ("composer_status", composerStatus),
                    ("classification_reason", composer.Reason)));
        }

        var canCaptureText = snapshot.CanReadValue || snapshot.CanReadTextPattern;
        var canReplaceText = snapshot.CanWriteValue || composer.UseKeyboardWriteFallback;
        var arbitraryMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read_strategy"] = snapshot.CanReadValue ? "windows-ui-automation-value-pattern" : "windows-ui-automation-text-pattern",
            ["write_strategy"] = composer.UseKeyboardWriteFallback ? "verified-composer-keyboard-paste" : (snapshot.CanWriteValue ? "windows-ui-automation-value-pattern" : "windows-ui-automation-text-pattern"),
            ["focused_element_hash"] = snapshot.ElementRuntimeIdHash,
            ["classification_reason"] = composer.Reason
        };
        if (composer.UseKeyboardWriteFallback)
        {
            arbitraryMetadata["keyboard_write_fallback"] = "true";
        }
        var surface = new TextSurfaceDescriptor(
            SurfaceId: $"focused-composer:{match.Profile.ProfileId}:{snapshot.WindowHandle.ToInt64():X}:{snapshot.ElementRuntimeIdHash}",
            ProfileId: match.Profile.ProfileId,
            DisplayName: match.Profile.DisplayName,
            Supported: true,
            CanCaptureText: canCaptureText,
            CanReplaceText: canReplaceText,
            CanSubmit: match.Profile.ProfileId is "codex-desktop" or "chatgpt-desktop",
            Metadata: new SurfaceMetadata(
                ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                WindowHandle: snapshot.WindowHandle.ToInt64().ToString("X"),
                ElementAutomationId: snapshot.ElementAutomationId,
                ArbitraryMetadata: arbitraryMetadata));

        return new TextSurfaceDiscoveryResult(
            true,
            OsInteractionStatusIds.SupportedComposer,
            surface,
            Merge(match.Diagnostics, Diagnostics(snapshot)));
    }

    private static ComposerClassification ClassifyComposer(FocusedElementSnapshot snapshot, SurfaceProfile profile)
    {
        if (!snapshot.HasKeyboardFocus || !snapshot.IsEnabled || snapshot.IsPassword)
        {
            return new ComposerClassification(OsInteractionStatusIds.NotComposer, "focused_element_not_editable_context", false);
        }

        if (!IsComposerControlType(snapshot.ElementControlType) && !IsKnownFrameworkTextGroup(profile, snapshot))
        {
            return new ComposerClassification(OsInteractionStatusIds.NotComposer, "focused_element_control_type_not_composer", false);
        }

        if (snapshot.CanReadValue && snapshot.CanWriteValue && !snapshot.IsValueReadOnly)
        {
            return new ComposerClassification(OsInteractionStatusIds.SupportedComposer, "value_pattern_read_write", false);
        }

        if (snapshot.CanReadTextPattern && CanUseKeyboardFallback(profile, snapshot))
        {
            var reason = IsKnownFrameworkTextGroup(profile, snapshot)
                ? "known_framework_group_text_pattern_keyboard_write"
                : "text_pattern_read_keyboard_write";
            return new ComposerClassification(OsInteractionStatusIds.SupportedComposer, reason, true);
        }

        if (snapshot.CanReadTextPattern)
        {
            return new ComposerClassification(OsInteractionStatusIds.SupportedComposer, "text_pattern_read_only", false);
        }

        return new ComposerClassification(OsInteractionStatusIds.NotComposer, "no_supported_text_pattern", false);
    }

    private static bool IsComposerControlType(string controlType)
    {
        return controlType.Contains("Edit", StringComparison.OrdinalIgnoreCase)
            || controlType.Contains("Document", StringComparison.OrdinalIgnoreCase)
            || controlType.Contains("Text", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownFrameworkTextGroup(SurfaceProfile profile, FocusedElementSnapshot snapshot)
    {
        return profile.ProfileId is "codex-desktop" or "chatgpt-desktop" or "redaction-gate-demo"
            && snapshot.ElementControlType.Contains("Group", StringComparison.OrdinalIgnoreCase)
            && snapshot.ElementFrameworkId is "Chrome" or "XAML"
            && snapshot.CanReadTextPattern
            && snapshot.CanUseKeyboardTextInput;
    }

    private static bool CanUseKeyboardFallback(SurfaceProfile profile, FocusedElementSnapshot snapshot)
    {
        return profile.ProfileId is "codex-desktop" or "chatgpt-desktop" or "redaction-gate-demo"
            && snapshot.CanUseKeyboardTextInput;
    }

    private sealed record ComposerClassification(string Status, string Reason, bool UseKeyboardWriteFallback);

    private static IReadOnlyDictionary<string, string> Diagnostics(FocusedElementSnapshot snapshot)
    {
        var applicationVersion = string.IsNullOrWhiteSpace(snapshot.ApplicationVersion)
            ? ApplicationVersion(snapshot.WindowHandle)
            : snapshot.ApplicationVersion;
        var stableProcessName = OpenAiDesktopIdentity.NormalizeProductId(snapshot.ProcessName);
        var stableExecutableName = OpenAiDesktopIdentity.NormalizeExecutableName(
            string.IsNullOrWhiteSpace(snapshot.ExecutableName) ? snapshot.ProcessName : snapshot.ExecutableName);
        var packageFullName = PackageFullName(snapshot.WindowHandle, snapshot.PackageFullName);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window_title_length"] = snapshot.WindowTitle.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["process_name_length"] = snapshot.ProcessName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["application_identity_hash"] = Hash(OpenAiDesktopIdentity.NormalizeProductId(snapshot.ProcessName)),
            ["application_version_hash"] = Hash(applicationVersion),
            ["application_version_status"] = ApplicationVersionStatus(applicationVersion),
            ["package_identity_status"] = string.IsNullOrWhiteSpace(packageFullName) ? "unavailable" : "available",
            ["package_full_name_hash"] = Hash(packageFullName),
            ["executable_name_hash"] = Hash(stableExecutableName),
            ["process_name_hash"] = Hash(stableProcessName),
            ["target_process_hash"] = Hash($"{snapshot.ProcessName}|{snapshot.WindowHandle.ToInt64():X}"),
            ["window_identity_hash"] = Hash($"{snapshot.WindowClassName}|{snapshot.WindowHandle.ToInt64():X}"),
            ["window_class_hash"] = Hash(snapshot.WindowClassName),
            ["composer_class_hash"] = Hash(snapshot.ElementClassName),
            ["window_class_name_length"] = snapshot.WindowClassName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["element_control_type"] = snapshot.ElementControlType,
            ["element_class_name_length"] = snapshot.ElementClassName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["element_automation_id_length"] = snapshot.ElementAutomationId.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["element_framework_id"] = string.IsNullOrEmpty(snapshot.ElementFrameworkId) ? "unknown" : snapshot.ElementFrameworkId,
            ["has_keyboard_focus"] = snapshot.HasKeyboardFocus.ToString().ToLowerInvariant(),
            ["is_keyboard_focusable"] = snapshot.IsKeyboardFocusable.ToString().ToLowerInvariant(),
            ["is_enabled"] = snapshot.IsEnabled.ToString().ToLowerInvariant(),
            ["is_password"] = snapshot.IsPassword.ToString().ToLowerInvariant(),
            ["can_read_value"] = snapshot.CanReadValue.ToString().ToLowerInvariant(),
            ["can_write_value"] = snapshot.CanWriteValue.ToString().ToLowerInvariant(),
            ["is_value_read_only"] = snapshot.IsValueReadOnly.ToString().ToLowerInvariant(),
            ["can_read_text_pattern"] = snapshot.CanReadTextPattern.ToString().ToLowerInvariant(),
            ["can_use_keyboard_text_input"] = snapshot.CanUseKeyboardTextInput.ToString().ToLowerInvariant(),
            ["focused_element_hash"] = snapshot.ElementRuntimeIdHash,
            ["focus_resolution_stage"] = snapshot.FocusResolutionStage
        };
    }

    private static string ApplicationVersion(IntPtr window)
    {
        try
        {
            NativeFocusedElementSnapshotProvider.NativeMethods.GetWindowThreadProcessId(window, out var processId);
            return Process.GetProcessById((int)processId).MainModule?.FileVersionInfo.ProductVersion ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ApplicationVersionStatus(string applicationVersion)
    {
        return string.Equals(applicationVersion, "unknown", StringComparison.Ordinal)
            ? "unavailable"
            : "available";
    }

    private static string PackageFullName(IntPtr window, string snapshotPackageFullName)
    {
        if (!string.IsNullOrWhiteSpace(snapshotPackageFullName))
        {
            return snapshotPackageFullName;
        }

        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            NativeFocusedElementSnapshotProvider.NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById((int)processId);
            return PackageMethods.TryGetPackageFullName(process.Handle);
        }
        catch (Exception)
        {
            return string.Empty;
        }
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
        foreach (var value in values)
        {
            merged[value.Key] = value.Value;
        }

        return merged;
    }

    private static IReadOnlyDictionary<string, string> Merge(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second)
    {
        var merged = new Dictionary<string, string>(first, StringComparer.Ordinal);
        foreach (var item in second)
        {
            merged[item.Key] = item.Value;
        }

        return merged;
    }

    private static class PackageMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetPackageFullName(
            IntPtr process,
            ref uint packageFullNameLength,
            StringBuilder packageFullName);

        internal static string TryGetPackageFullName(IntPtr process)
        {
            uint length = 0;
            var result = GetPackageFullName(process, ref length, new StringBuilder(1));
            if (result != 122 || length == 0)
            {
                return string.Empty;
            }

            var packageFullName = new StringBuilder((int)length);
            return GetPackageFullName(process, ref length, packageFullName) == 0
                ? packageFullName.ToString()
                : string.Empty;
        }
    }
}

public sealed class WindowsVerifiedComposerSurfaceAdapter :
    ITextSurfaceReader,
    ITextSurfaceWriter,
    ISubmitAction
{
    private readonly IVerifiedComposerTextAccess _textAccess;

    public WindowsVerifiedComposerSurfaceAdapter()
        : this(OperatingSystem.IsWindows()
            ? new NativeVerifiedComposerTextAccess()
            : new UnsupportedVerifiedComposerTextAccess())
    {
    }

    public WindowsVerifiedComposerSurfaceAdapter(IVerifiedComposerTextAccess textAccess)
    {
        _textAccess = textAccess ?? throw new ArgumentNullException(nameof(textAccess));
    }

    public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
    {
        return IsVerifiedComposer(surface)
            ? _textAccess.CaptureText(surface)
            : new TextCaptureResult(false, OsInteractionStatusIds.NotComposer, null, new Dictionary<string, string>());
    }

    public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsVerifiedComposer(surface)
            ? _textAccess.ReplaceText(surface, text)
            : new TextReplacementResult(false, OsInteractionStatusIds.NotComposer, new Dictionary<string, string>());
    }

    public SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        return IsVerifiedComposer(surface)
            ? _textAccess.Submit(surface)
            : new SubmitActionResult(false, OsInteractionStatusIds.NotComposer, new Dictionary<string, string>());
    }

    private static bool IsVerifiedComposer(TextSurfaceDescriptor surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return surface.Metadata.ComposerStatus == OsInteractionStatusIds.SupportedComposer
            && surface.Metadata.TryGetValue("focused_element_hash") is not null;
    }
}

public sealed class UnsupportedFocusedElementSnapshotProvider : IFocusedElementSnapshotProvider
{
    public FocusedElementSnapshot GetFocusedElement()
    {
        return new FocusedElementSnapshot(
            false,
            OsInteractionStatusIds.UnsupportedPlatform,
            string.Empty,
            string.Empty,
            string.Empty,
            IntPtr.Zero,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            false,
            false,
            false,
            true,
            false,
            false,
            "unsupported");
    }
}

public sealed class NativeFocusedElementSnapshotProvider : IFocusedElementSnapshotProvider
{
    private readonly Func<AutomationElement?> _focusedElementProvider;
    private readonly Func<IntPtr> _foregroundWindowProvider;

    public NativeFocusedElementSnapshotProvider()
        : this(() => AutomationElement.FocusedElement, NativeMethods.GetForegroundWindow)
    {
    }

    internal NativeFocusedElementSnapshotProvider(
        Func<AutomationElement?> focusedElementProvider,
        Func<IntPtr> foregroundWindowProvider)
    {
        _focusedElementProvider = focusedElementProvider ?? throw new ArgumentNullException(nameof(focusedElementProvider));
        _foregroundWindowProvider = foregroundWindowProvider ?? throw new ArgumentNullException(nameof(foregroundWindowProvider));
    }

    public FocusedElementSnapshot GetFocusedElement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UnsupportedFocusedElementSnapshotProvider().GetFocusedElement();
        }

        try
        {
            var resolution = ResolveFocusedElement();
            var element = resolution.Element;
            if (element is null)
            {
                return Failure(OsInteractionStatusIds.FocusLost, resolution.Stage);
            }

            var window = FindOwningWindow(element);
            if (window == IntPtr.Zero)
            {
                return Failure(OsInteractionStatusIds.UnsupportedSurface, "owning_window_unavailable");
            }

            var valuePattern = TryGetCurrentPattern<ValuePattern>(element, ValuePattern.Pattern);
            var textPattern = TryGetCurrentPattern<TextPattern>(element, TextPattern.Pattern);
            var runtimeHash = HashRuntimeId(element.GetRuntimeId());

            return new FocusedElementSnapshot(
                true,
                OsInteractionStatusIds.SupportedSurface,
                GetWindowText(window),
                GetProcessName(window),
                GetClassName(window),
                window,
                element.Current.ControlType.ProgrammaticName,
                element.Current.ClassName ?? string.Empty,
                element.Current.AutomationId ?? string.Empty,
                element.Current.FrameworkId ?? string.Empty,
                element.Current.HasKeyboardFocus,
                element.Current.IsKeyboardFocusable,
                element.Current.IsEnabled,
                element.Current.IsPassword,
                valuePattern is not null,
                valuePattern is not null && !valuePattern.Current.IsReadOnly,
                valuePattern?.Current.IsReadOnly ?? true,
                textPattern is not null,
                element.Current.HasKeyboardFocus && element.Current.IsKeyboardFocusable && element.Current.IsEnabled,
                runtimeHash,
                resolution.Stage,
                ExecutableName: GetExecutableName(window));
        }
        catch (ElementNotAvailableException)
        {
            return Failure(OsInteractionStatusIds.UnsupportedSurface, "element_not_available");
        }
        catch (InvalidOperationException)
        {
            return Failure(OsInteractionStatusIds.UnsupportedSurface, "uia_invalid_operation");
        }
        catch (COMException)
        {
            return Failure(OsInteractionStatusIds.UnsupportedSurface, "uia_com_error");
        }
    }

    private FocusedElementResolution ResolveFocusedElement()
    {
        try
        {
            var focused = _focusedElementProvider();
            if (focused is not null)
            {
                return new FocusedElementResolution(focused, "global_focused_element");
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // The foreground-window fallback remains bounded to the active window.
        }

        try
        {
            var foregroundWindow = _foregroundWindowProvider();
            if (foregroundWindow == IntPtr.Zero)
            {
                return new FocusedElementResolution(null, "foreground_window_unavailable");
            }

            var root = AutomationElement.FromHandle(foregroundWindow);
            if (root is null)
            {
                return new FocusedElementResolution(null, "foreground_automation_root_unavailable");
            }

            if (root.Current.HasKeyboardFocus)
            {
                return new FocusedElementResolution(root, "foreground_root_fallback");
            }

            var focusedDescendant = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true));
            return focusedDescendant is null
                ? new FocusedElementResolution(null, "foreground_focused_descendant_unavailable")
                : new FocusedElementResolution(focusedDescendant, "foreground_descendant_fallback");
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return new FocusedElementResolution(null, "foreground_fallback_failed");
        }
    }

    private static FocusedElementSnapshot Failure(string status, string stage = "unknown")
    {
        return new FocusedElementSnapshot(
            false,
            status,
            string.Empty,
            string.Empty,
            string.Empty,
            IntPtr.Zero,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            false,
            false,
            false,
            true,
            false,
            false,
            "unavailable",
            stage);
    }

    private sealed record FocusedElementResolution(AutomationElement? Element, string Stage);

    private static T? TryGetCurrentPattern<T>(AutomationElement element, AutomationPattern pattern)
        where T : class
    {
        return element.TryGetCurrentPattern(pattern, out var value) ? value as T : null;
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

    private static string HashRuntimeId(int[] runtimeId)
    {
        return TransientTargetFingerprint.FingerprintRuntimeId(runtimeId).Value;
    }

    private static string GetWindowText(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        var builder = new StringBuilder(Math.Max(length + 1, 1));
        NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        NativeMethods.GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetProcessName(IntPtr handle)
    {
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    }

    private static string GetExecutableName(IntPtr handle)
    {
        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            var executablePath = Process.GetProcessById((int)processId).MainModule?.FileName;
            return string.IsNullOrWhiteSpace(executablePath)
                ? string.Empty
                : System.IO.Path.GetFileName(executablePath);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

[Flags]
internal enum VerifiedKeyboardModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4
}

internal sealed record VerifiedComposerReplayResult(
    bool Succeeded,
    string Status,
    IReadOnlyDictionary<string, string> Diagnostics);

internal interface IVerifiedComposerReplay
{
    VerifiedComposerReplayResult Replay(AutomationElement? element, string sendKeysText);
}

internal sealed class NativeVerifiedComposerReplay : IVerifiedComposerReplay
{
    public VerifiedComposerReplayResult Replay(AutomationElement? element, string sendKeysText)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentException.ThrowIfNullOrWhiteSpace(sendKeysText);

        var modifiers = DetectModifiers(sendKeysText);
        try
        {
            element.SetFocus();
            SendKeys.SendWait(sendKeysText);
            Thread.Sleep(120);
            return new VerifiedComposerReplayResult(
                true,
                OsInteractionStatusIds.Submitted,
                new Dictionary<string, string>
                {
                    ["replay_outcome"] = "completed",
                    ["modifiers_released"] = "true"
                });
        }
        catch (InvalidOperationException)
        {
            return FailedResult();
        }
        catch (COMException)
        {
            return FailedResult();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return FailedResult();
        }
        finally
        {
            ReleaseModifiers(modifiers);
        }
    }

    private static VerifiedComposerReplayResult FailedResult()
    {
        return new VerifiedComposerReplayResult(
            false,
            OsInteractionStatusIds.ReplayIndeterminate,
            new Dictionary<string, string>
            {
                ["replay_outcome"] = "unavailable",
                ["modifiers_released"] = "true"
            });
    }

    private static VerifiedKeyboardModifiers DetectModifiers(string sendKeysText)
    {
        var modifiers = VerifiedKeyboardModifiers.None;
        if (sendKeysText.Contains('^', StringComparison.Ordinal))
        {
            modifiers |= VerifiedKeyboardModifiers.Control;
        }

        if (sendKeysText.Contains('+', StringComparison.Ordinal))
        {
            modifiers |= VerifiedKeyboardModifiers.Shift;
        }

        if (sendKeysText.Contains('%', StringComparison.Ordinal))
        {
            modifiers |= VerifiedKeyboardModifiers.Alt;
        }

        return modifiers;
    }

    private static void ReleaseModifiers(VerifiedKeyboardModifiers modifiers)
    {
        if ((modifiers & VerifiedKeyboardModifiers.Control) != 0)
        {
            NativeMethods.ReleaseKey(0x11);
        }

        if ((modifiers & VerifiedKeyboardModifiers.Shift) != 0)
        {
            NativeMethods.ReleaseKey(0x10);
        }

        if ((modifiers & VerifiedKeyboardModifiers.Alt) != 0)
        {
            NativeMethods.ReleaseKey(0x12);
        }
    }

    private static class NativeMethods
    {
        private const uint KeyUp = 0x0002;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        public static void ReleaseKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, KeyUp, UIntPtr.Zero);
        }
    }
}

public sealed class NativeVerifiedComposerTextAccess : IVerifiedComposerTextAccess
{
    private const int MaxTextPatternCaptureLength = 65536;
    private const int MaxRuntimeIdSearchElements = 512;
    private const int FocusVerificationAttempts = 8;
    private const int FocusVerificationDelayMilliseconds = 25;
    private readonly Func<TextSurfaceDiscoveryResult> _surfaceDiscovery;
    private readonly IVerifiedComposerReplay _replay;
    private readonly object _capturedElementGate = new();
    private AutomationElement? _capturedElement;
    private string? _capturedSurfaceId;

    public NativeVerifiedComposerTextAccess()
        : this(
            () => WindowsFocusedComposerDiscovery.CreateDefault().DiscoverActiveSurface(),
            new NativeVerifiedComposerReplay())
    {
    }

    internal NativeVerifiedComposerTextAccess(
        Func<TextSurfaceDiscoveryResult> surfaceDiscovery,
        IVerifiedComposerReplay? replay = null)
    {
        _surfaceDiscovery = surfaceDiscovery ?? throw new ArgumentNullException(nameof(surfaceDiscovery));
        _replay = replay ?? new NativeVerifiedComposerReplay();
    }

    public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
    {
        try
        {
            return RunSta(() =>
            {
                var element = GetCurrentVerifiedElement(surface);
                if (element is null)
                {
                    return new TextCaptureResult(false, OsInteractionStatusIds.NotComposer, null, new Dictionary<string, string>());
                }

                var pattern = GetValuePattern(element);
                var text = pattern?.Current.Value;
                var captureStrategy = "value-pattern";
                if (text is null)
                {
                    var textPattern = GetTextPattern(element);
                    if (textPattern is null)
                    {
                        return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
                    }

                    text = textPattern.DocumentRange.GetText(MaxTextPatternCaptureLength) ?? string.Empty;
                    captureStrategy = "text-pattern";

                    if (CanUseKeyboardWriteFallback(surface, element))
                    {
                        var clipboardText = CaptureFormattedKeyboardText(element);
                        if (clipboardText is null)
                        {
                            return new TextCaptureResult(
                                false,
                                OsInteractionStatusIds.CaptureFailed,
                                null,
                                new Dictionary<string, string>
                                {
                                    ["capture_strategy"] = "verified-keyboard-copy",
                                    ["format_capture_status"] = "unavailable",
                                    ["format_preserved"] = "false"
                                });
                        }

                        if (!ComposerTextFormatting.HasSameContentIgnoringWhitespace(text, clipboardText))
                        {
                            return new TextCaptureResult(
                                false,
                                OsInteractionStatusIds.CaptureFailed,
                                null,
                                new Dictionary<string, string>
                                {
                                    ["capture_strategy"] = "verified-keyboard-copy",
                                    ["format_capture_status"] = "source_mismatch",
                                    ["format_preserved"] = "false"
                                });
                        }

                        text = clipboardText;
                        captureStrategy = "verified-keyboard-copy";
                    }
                }

                if (string.IsNullOrEmpty(text))
                {
                    return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
                }

                var normalizedText = ComposerTextFormatting.NormalizeLineEndings(text);

                return new TextCaptureResult(
                    true,
                    "captured",
                    normalizedText,
                    ComposerTextFormatting.Diagnostics(text, normalizedText, captureStrategy));
            });
        }
        catch (InvalidOperationException)
        {
            return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
        }
        catch (COMException)
        {
            return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
        }
        catch (ExternalException)
        {
            return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
        }
    }

    public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return RunSta(() =>
            {
                var element = GetCurrentVerifiedElement(surface);
                if (element is null)
                {
                    return new TextReplacementResult(false, OsInteractionStatusIds.NotComposer, new Dictionary<string, string>());
                }

                var pattern = GetValuePattern(element);
                if (pattern is not null && !pattern.Current.IsReadOnly)
                {
                    pattern.SetValue(text);
                    return new TextReplacementResult(
                        true,
                        OsInteractionStatusIds.Applied,
                        new Dictionary<string, string>
                        {
                            ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["write_strategy"] = "value-pattern"
                        });
                }

                if (!CanUseKeyboardWriteFallback(surface, element))
                {
                    return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
                }

                if (!TryPasteIntoVerifiedElement(element, text))
                {
                    return new TextReplacementResult(
                        false,
                        OsInteractionStatusIds.WriteFailed,
                        new Dictionary<string, string>
                        {
                            ["write_strategy"] = "verified-keyboard-paste",
                            ["write_focus"] = "not_verified",
                            ["write_verification"] = "pasted_text_mismatch"
                        });
                }

                return new TextReplacementResult(
                    true,
                    OsInteractionStatusIds.Applied,
                    new Dictionary<string, string>
                    {
                        ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["write_strategy"] = "verified-keyboard-paste",
                        ["write_focus"] = "restored_and_verified",
                        ["write_verification"] = "exact_keyboard_round_trip"
                    });
            });
        }
        catch (InvalidOperationException)
        {
            return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
        }
        catch (COMException)
        {
            return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
        }
        catch (ExternalException)
        {
            return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
        }
    }

    public SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        try
        {
            return RunSta(() =>
            {
                var element = GetCurrentVerifiedElement(surface);
                if (element is null)
                {
                    return new SubmitActionResult(false, OsInteractionStatusIds.NotComposer, new Dictionary<string, string>());
                }

                var sendKeysText = surface.Metadata.TryGetValue("submit_binding_sendkeys");
                if (string.IsNullOrWhiteSpace(sendKeysText))
                {
                    return new SubmitActionResult(
                        false,
                        OsInteractionStatusIds.BindingUnknown,
                        new Dictionary<string, string> { ["submit_binding"] = "unknown" });
                }

                var replay = _replay.Replay(element, sendKeysText);
                var diagnostics = new Dictionary<string, string>(replay.Diagnostics)
                {
                    ["submit_strategy"] = "verified-composer-binding",
                    ["submit_binding"] = surface.Metadata.TryGetValue("submit_binding") ?? "configured"
                };
                return new SubmitActionResult(replay.Succeeded, replay.Status, diagnostics);
            });
        }
        catch (InvalidOperationException)
        {
            return new SubmitActionResult(false, OsInteractionStatusIds.SubmitFailed, new Dictionary<string, string>());
        }
        catch (COMException)
        {
            return new SubmitActionResult(false, OsInteractionStatusIds.SubmitFailed, new Dictionary<string, string>());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new SubmitActionResult(false, OsInteractionStatusIds.SubmitFailed, new Dictionary<string, string>());
        }
    }

    private AutomationElement? GetCurrentVerifiedElement(TextSurfaceDescriptor surface)
    {
        var expectedWindowHandle = surface.Metadata.TryGetValue("window_handle");
        var expectedRuntimeIdHash = surface.Metadata.TryGetValue("focused_element_hash");
        var requireCapturedIdentity = !string.Equals(
            surface.ProfileId,
            ReferenceOnlyInputSource.ProfileId,
            StringComparison.Ordinal);

        if (IntPtr.TryParse(
                expectedWindowHandle,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var cachedWindowHandle)
            && TryGetCapturedElement(surface, cachedWindowHandle, expectedRuntimeIdHash, requireCapturedIdentity, out var capturedElement))
        {
            return capturedElement;
        }

        var discovery = _surfaceDiscovery();
        if (!discovery.Succeeded || discovery.Surface is null)
        {
            return null;
        }

        if (!string.Equals(discovery.Surface.ProfileId, surface.ProfileId, StringComparison.Ordinal))
        {
            return null;
        }

        // Extract window handle from surface metadata and verify it matches
        var actualWindowHandle = discovery.Surface.Metadata.TryGetValue("window_handle");
        if (expectedWindowHandle == null || actualWindowHandle == null
            || !string.Equals(expectedWindowHandle, actualWindowHandle, StringComparison.Ordinal))
        {
            return null;
        }

        if (requireCapturedIdentity && string.IsNullOrWhiteSpace(expectedRuntimeIdHash))
        {
            return null;
        }

        // Try to find the element by AutomationId within the verified window
        // This allows us to find the element even if focus has moved to another window
        var windowHandle = IntPtr.Zero;
        if (IntPtr.TryParse(expectedWindowHandle, System.Globalization.NumberStyles.HexNumber, null, out windowHandle))
        {
            var automationId = surface.Metadata.TryGetValue("element_automation_id");
            if (!string.IsNullOrEmpty(automationId))
            {
                var element = FindElementByAutomationId(windowHandle, automationId);
                if (element is not null
                    && (!requireCapturedIdentity || MatchesCapturedRuntimeId(element, expectedRuntimeIdHash!)))
                {
                    return RememberCapturedElement(surface, element);
                }
            }

            if (requireCapturedIdentity)
            {
                var fingerprintMatch = FindElementByRuntimeIdHash(windowHandle, expectedRuntimeIdHash!);
                if (fingerprintMatch is not null)
                {
                    return RememberCapturedElement(surface, fingerprintMatch);
                }
            }

            // Fallback to FocusedElement if we cannot find by AutomationId
            // This handles cases where the element might have been recreated
            // BUT we must verify the element is in the same window to avoid writing to wrong element
            var fallback = AutomationElement.FocusedElement;
            if (fallback != null)
            {
                var fallbackHandle = new IntPtr(fallback.Current.NativeWindowHandle);
                var fallbackOwningWindow = FindOwningWindow(fallback);
                if (MatchesVerifiedSurfaceWindow(windowHandle, fallbackHandle, fallbackOwningWindow)
                    && (!requireCapturedIdentity || MatchesCapturedRuntimeId(fallback, expectedRuntimeIdHash!)))
                {
                    return RememberCapturedElement(surface, fallback);
                }
            }
        }

        return null; // Fail closed if we cannot verify the element
    }

    private bool TryGetCapturedElement(
        TextSurfaceDescriptor surface,
        IntPtr expectedWindowHandle,
        string? expectedRuntimeIdHash,
        bool requireCapturedIdentity,
        out AutomationElement? element)
    {
        element = null;
        AutomationElement? captured;
        string? capturedSurfaceId;
        lock (_capturedElementGate)
        {
            captured = _capturedElement;
            capturedSurfaceId = _capturedSurfaceId;
        }

        if (captured is null
            || !string.Equals(capturedSurfaceId, surface.SurfaceId, StringComparison.Ordinal)
            || !IsExpectedWindowForeground(expectedWindowHandle))
        {
            return false;
        }

        try
        {
            var elementWindow = new IntPtr(captured.Current.NativeWindowHandle);
            var owningWindow = FindOwningWindow(captured);
            if (!MatchesVerifiedSurfaceWindow(expectedWindowHandle, elementWindow, owningWindow)
                || (requireCapturedIdentity
                    && (string.IsNullOrWhiteSpace(expectedRuntimeIdHash)
                        || !MatchesCapturedRuntimeId(captured, expectedRuntimeIdHash)))
                || !captured.Current.IsEnabled
                || !captured.Current.IsKeyboardFocusable)
            {
                return false;
            }

            element = captured;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private AutomationElement RememberCapturedElement(TextSurfaceDescriptor surface, AutomationElement element)
    {
        lock (_capturedElementGate)
        {
            _capturedSurfaceId = surface.SurfaceId;
            _capturedElement = element;
        }

        return element;
    }

    private static bool IsExpectedWindowForeground(IntPtr expectedWindow)
    {
        if (!OperatingSystem.IsWindows() || expectedWindow == IntPtr.Zero)
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var root = NativeMethods.GetAncestor(foreground, 2);
        return MatchesVerifiedSurfaceWindow(
            expectedWindow,
            foreground,
            root == IntPtr.Zero ? foreground : root);
    }

    private static bool MatchesCapturedRuntimeId(AutomationElement element, string expectedRuntimeIdHash)
    {
        try
        {
            var actual = TransientTargetFingerprint.FingerprintRuntimeId(element.GetRuntimeId()).Value;
            return string.Equals(actual, expectedRuntimeIdHash, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    internal static bool MatchesVerifiedSurfaceWindow(
        IntPtr expectedWindow,
        IntPtr elementWindow,
        IntPtr owningWindow)
    {
        return expectedWindow != IntPtr.Zero
            && (elementWindow == expectedWindow || owningWindow == expectedWindow);
    }

    private static IntPtr FindOwningWindow(AutomationElement element)
    {
        var current = element;
        while (current is not null)
        {
            var handle = new IntPtr(current.Current.NativeWindowHandle);
            if (handle != IntPtr.Zero)
            {
                var root = NativeMethods.GetAncestor(handle, 2);
                return root == IntPtr.Zero ? handle : root;
            }

            current = TreeWalker.ControlViewWalker.GetParent(current);
        }

        return IntPtr.Zero;
    }

    private static ValuePattern? GetValuePattern(AutomationElement element)
    {
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ? value as ValuePattern : null;
    }

    private static TextPattern? GetTextPattern(AutomationElement element)
    {
        return element.TryGetCurrentPattern(TextPattern.Pattern, out var value) ? value as TextPattern : null;
    }

    private static bool CanUseKeyboardWriteFallback(TextSurfaceDescriptor surface, AutomationElement element)
    {
        var fallback = surface.Metadata.TryGetValue("keyboard_write_fallback");
        return fallback == "true"
            && element.Current.IsKeyboardFocusable
            && element.Current.IsEnabled;
    }

    private static bool TryPasteIntoVerifiedElement(AutomationElement element, string text)
    {
        if (!TryFocusVerifiedElement(element))
        {
            return false;
        }

        ClipboardSnapshot? clipboardBackup = null;
        try
        {
            clipboardBackup = ClipboardSnapshot.Capture();
            Clipboard.SetText(text);
            SendKeys.SendWait("^a");
            SendKeys.SendWait("^v");
            return WaitForPastedText(element, text);
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            clipboardBackup?.Restore();
        }
    }

    private static bool WaitForPastedText(AutomationElement element, string expectedText)
    {
        var expected = ComposerTextFormatting.NormalizeLineEndings(expectedText);
        for (var attempt = 0; attempt < FocusVerificationAttempts; attempt++)
        {
            var actual = CaptureFormattedKeyboardText(element);
            if (actual is not null
                && string.Equals(
                    ComposerTextFormatting.NormalizeLineEndings(actual),
                    expected,
                    StringComparison.Ordinal))
            {
                return true;
            }

            Thread.Sleep(FocusVerificationDelayMilliseconds);
        }

        return false;
    }

    private static bool TryFocusVerifiedElement(AutomationElement element)
    {
        try
        {
            if (!element.Current.IsKeyboardFocusable || !element.Current.IsEnabled)
            {
                return false;
            }

            element.SetFocus();
            for (var attempt = 0; attempt < FocusVerificationAttempts; attempt++)
            {
                if (element.Current.HasKeyboardFocus)
                {
                    return true;
                }

                Thread.Sleep(FocusVerificationDelayMilliseconds);
            }
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }

        return false;
    }

    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }

        return result!;
    }

    private static AutomationElement? FindElementByAutomationId(IntPtr windowHandle, string automationId)
    {
        try
        {
            var windowElement = AutomationElement.FromHandle(windowHandle);
            if (windowElement is null)
            {
                return null;
            }

            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
            return windowElement.FindFirst(TreeScope.Descendants, condition);
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return null;
        }
    }

    private static AutomationElement? FindElementByRuntimeIdHash(IntPtr windowHandle, string expectedRuntimeIdHash)
    {
        try
        {
            var windowElement = AutomationElement.FromHandle(windowHandle);
            if (windowElement is null)
            {
                return null;
            }

            var candidates = windowElement.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var inspected = Math.Min(candidates.Count, MaxRuntimeIdSearchElements);
            for (var index = 0; index < inspected; index++)
            {
                var candidate = candidates[index];
                if (MatchesCapturedRuntimeId(candidate, expectedRuntimeIdHash))
                {
                    return candidate;
                }
            }
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (COMException)
        {
        }

        return null;
    }

    private static string? CaptureFormattedKeyboardText(AutomationElement element)
    {
        if (!TryFocusVerifiedElement(element))
        {
            return null;
        }

        ClipboardSnapshot? clipboardBackup = null;
        try
        {
            clipboardBackup = ClipboardSnapshot.Capture();
            Clipboard.Clear();
            SendKeys.SendWait("^a");
            SendKeys.SendWait("^c");
            Thread.Sleep(120);
            return Clipboard.ContainsText()
                ? Clipboard.GetText(TextDataFormat.UnicodeText)
                : null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        finally
        {
            clipboardBackup?.Restore();
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    }

    private sealed class ClipboardSnapshot
    {
        private readonly IDataObject? _data;

        private ClipboardSnapshot(IDataObject? data)
        {
            _data = data;
        }

        public static ClipboardSnapshot Capture()
        {
            return new ClipboardSnapshot(Clipboard.ContainsData(DataFormats.Text) || Clipboard.ContainsData(DataFormats.UnicodeText)
                ? Clipboard.GetDataObject()
                : null);
        }

        public void Restore()
        {
            if (_data is not null)
            {
                Clipboard.SetDataObject(_data, true);
            }
            else
            {
                Clipboard.Clear();
            }
        }
    }
}

public sealed class UnsupportedVerifiedComposerTextAccess : IVerifiedComposerTextAccess
{
    public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
    {
        return new TextCaptureResult(false, OsInteractionStatusIds.UnsupportedPlatform, null, new Dictionary<string, string>());
    }

    public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
    {
        return new TextReplacementResult(false, OsInteractionStatusIds.UnsupportedPlatform, new Dictionary<string, string>());
    }

    public SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        return new SubmitActionResult(false, OsInteractionStatusIds.UnsupportedPlatform, new Dictionary<string, string>());
    }
}
