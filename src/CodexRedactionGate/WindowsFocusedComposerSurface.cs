using System;
using System.Collections.Generic;
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
    string ElementRuntimeIdHash);

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
        var surface = new TextSurfaceDescriptor(
            SurfaceId: $"focused-composer:{match.Profile.ProfileId}:{snapshot.WindowHandle.ToInt64():X}:{snapshot.ElementRuntimeIdHash}",
            ProfileId: match.Profile.ProfileId,
            DisplayName: match.Profile.DisplayName,
            Supported: true,
            CanCaptureText: canCaptureText,
            CanReplaceText: canReplaceText,
            CanSubmit: match.Profile.ProfileId is "codex-desktop" or "chatgpt-desktop",
            Metadata: Merge(
                new Dictionary<string, string>
                {
                    ["read_strategy"] = snapshot.CanReadValue ? "windows-ui-automation-value-pattern" : "windows-ui-automation-text-pattern",
                    ["write_strategy"] = snapshot.CanWriteValue ? "windows-ui-automation-value-pattern" : "verified-composer-keyboard-paste",
                    ["submit_strategy"] = match.Profile.SubmitStrategy,
                    ["window_handle"] = snapshot.WindowHandle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                    ["focused_element_hash"] = snapshot.ElementRuntimeIdHash,
                    ["composer_status"] = OsInteractionStatusIds.SupportedComposer,
                    ["classification_reason"] = composer.Reason,
                    ["keyboard_write_fallback"] = composer.UseKeyboardWriteFallback.ToString().ToLowerInvariant()
                },
                Diagnostics(snapshot)));

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
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["window_title_length"] = snapshot.WindowTitle.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["process_name_length"] = snapshot.ProcessName.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            ["focused_element_hash"] = snapshot.ElementRuntimeIdHash
        };
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
        return surface.Metadata.TryGetValue("composer_status", out var status)
            && status == OsInteractionStatusIds.SupportedComposer
            && surface.Metadata.ContainsKey("focused_element_hash");
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
    public FocusedElementSnapshot GetFocusedElement()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UnsupportedFocusedElementSnapshotProvider().GetFocusedElement();
        }

        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null)
            {
                return Failure(OsInteractionStatusIds.UnsupportedSurface);
            }

            var window = FindOwningWindow(element);
            if (window == IntPtr.Zero)
            {
                return Failure(OsInteractionStatusIds.UnsupportedSurface);
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
                runtimeHash);
        }
        catch (ElementNotAvailableException)
        {
            return Failure(OsInteractionStatusIds.UnsupportedSurface);
        }
        catch (InvalidOperationException)
        {
            return Failure(OsInteractionStatusIds.UnsupportedSurface);
        }
        catch (COMException)
        {
            return Failure(OsInteractionStatusIds.UnsupportedSurface);
        }
    }

    private static FocusedElementSnapshot Failure(string status)
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
            "unavailable");
    }

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
        var joined = string.Join(".", runtimeId.Select(item => item.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes).Substring(0, 16).ToLowerInvariant();
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

    private static class NativeMethods
    {
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
}

public sealed class NativeVerifiedComposerTextAccess : IVerifiedComposerTextAccess
{
    private const int MaxTextPatternCaptureLength = 65536;

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
                }

                if (string.IsNullOrEmpty(text))
                {
                    return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
                }

                return new TextCaptureResult(
                    true,
                    "captured",
                    text,
                    new Dictionary<string, string>
                    {
                        ["captured_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["capture_strategy"] = captureStrategy
                    });
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

                PasteIntoVerifiedFocusedElement(text);
                return new TextReplacementResult(
                    true,
                    OsInteractionStatusIds.Applied,
                    new Dictionary<string, string>
                    {
                        ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["write_strategy"] = "verified-keyboard-paste"
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

                SendKeys.SendWait("{ENTER}");
                Thread.Sleep(120);
                return new SubmitActionResult(
                    true,
                    OsInteractionStatusIds.Submitted,
                    new Dictionary<string, string> { ["submit_strategy"] = "verified-composer-enter" });
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
    }

    private static AutomationElement? GetCurrentVerifiedElement(TextSurfaceDescriptor surface)
    {
        var discovery = WindowsFocusedComposerDiscovery.CreateDefault().DiscoverActiveSurface();
        if (!discovery.Succeeded || discovery.Surface is null)
        {
            return null;
        }

        if (!string.Equals(discovery.Surface.SurfaceId, surface.SurfaceId, StringComparison.Ordinal)
            || !string.Equals(discovery.Surface.ProfileId, surface.ProfileId, StringComparison.Ordinal))
        {
            return null;
        }

        return AutomationElement.FocusedElement;
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
        return surface.Metadata.TryGetValue("keyboard_write_fallback", out var fallback)
            && fallback == "true"
            && element.Current.HasKeyboardFocus
            && element.Current.IsKeyboardFocusable
            && element.Current.IsEnabled;
    }

    private static void PasteIntoVerifiedFocusedElement(string text)
    {
        var clipboardBackup = ClipboardSnapshot.Capture();
        try
        {
            Clipboard.SetText(text);
            SendKeys.SendWait("^a");
            SendKeys.SendWait("^v");
            Thread.Sleep(120);
        }
        finally
        {
            clipboardBackup.Restore();
        }
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
            catch (Exception ex) when (
                ex is ElementNotAvailableException
                || ex is InvalidOperationException
                || ex is COMException)
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
