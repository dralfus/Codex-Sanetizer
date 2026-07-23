using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexRedactionGate;

public sealed class WindowsConfirmationOverlay : IConfirmationOverlay
{
    public static IReadOnlyList<string> ForegroundActivationRequestCapabilities { get; } = new[]
    {
        "show_in_taskbar",
        "topmost",
        "activate",
        "focus",
        "set_foreground_window",
        "attach_thread_input",
        "set_window_pos",
        "bring_window_to_top",
        "flash_window"
    };

    public static ConfirmationOverlayForegroundActivationResult RunForegroundActivationSmoke(bool foregroundActivated)
    {
        var window = new SmokeForegroundWindow();
        var native = new SmokeForegroundNativeMethods(foregroundActivated);
        return ConfirmationOverlayForegroundActivation.Request(window, native);
    }

    public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!OperatingSystem.IsWindows())
        {
            return ConfirmationDecisionContract.Cancel(model);
        }

        ConfirmationDecision? decision = null;
        Exception? dialogException = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                using var dialog = new ConfirmationDialog(model);
                var result = dialog.ShowDialog();
                decision = result == DialogResult.OK
                    ? ConfirmationDecisionContract.Confirm(model)
                    : ConfirmationDecisionContract.Cancel(model);
            }
            catch (Exception exception)
            {
                dialogException = exception;
            }
        });

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (dialogException is not null)
        {
            return ConfirmationDecisionContract.Cancel(model);
        }

        return decision ?? ConfirmationDecisionContract.Cancel(model);
    }

    private sealed class ConfirmationDialog : Form, IConfirmationOverlayWindow
    {
        public ConfirmationDialog(ConfirmationUiModel model)
        {
            Text = "Codex Redaction Gate";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 820;
            Height = 560;
            MinimizeBox = false;
            MaximizeBox = true;
            TopMost = true;
            ShowInTaskbar = true;
            Load += (_, _) => BringDialogToFront();
            Shown += (_, _) =>
            {
                BringDialogToFront();
                BeginInvoke(new Action(BringDialogToFront));
            };
            Activated += (_, _) => TopMost = true;

            var promptBox = new RichTextBox
            {
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Text = model.SanitizedPrompt,
                Font = new Font(FontFamily.GenericMonospace, 10),
                DetectUrls = false
            };
            Highlight(promptBox, model);

            var details = new TextBox
            {
                ReadOnly = true,
                Multiline = true,
                Dock = DockStyle.Right,
                Width = 240,
                Text = BuildDetails(model),
                ScrollBars = ScrollBars.Vertical
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(8)
            };
            var confirm = new Button
            {
                Text = model.PrimaryAction,
                DialogResult = DialogResult.OK,
                Width = 180
            };
            var cancel = new Button
            {
                Text = model.SecondaryAction,
                DialogResult = DialogResult.Cancel,
                Width = 100
            };

            buttons.Controls.Add(confirm);
            buttons.Controls.Add(cancel);
            Controls.Add(promptBox);
            Controls.Add(details);
            Controls.Add(buttons);
            AcceptButton = confirm;
            CancelButton = cancel;
        }

        private void BringDialogToFront()
        {
            ConfirmationOverlayForegroundActivation.Request(this, Win32ConfirmationOverlayNativeMethods.Instance);
        }

        private static void Highlight(RichTextBox promptBox, ConfirmationUiModel model)
        {
            foreach (var span in model.HighlightedSpans.OrderBy(span => span.Offset))
            {
                if (span.Offset < 0 || span.Offset + span.Length > promptBox.TextLength)
                {
                    continue;
                }

                promptBox.Select(span.Offset, span.Length);
                promptBox.SelectionBackColor = Color.FromArgb(255, 243, 205);
                promptBox.SelectionColor = Color.FromArgb(102, 60, 0);
            }

            promptBox.Select(0, 0);
        }

        private static string BuildDetails(ConfirmationUiModel model)
        {
            var lines = new System.Collections.Generic.List<string>
            {
                $"raw_values_visible: {model.RawValuesVisible.ToString().ToLowerInvariant()}",
                string.Empty,
                "counts:"
            };

            lines.AddRange(model.CountsByType
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{item.Key}: {item.Value}"));
            lines.Add(string.Empty);
            lines.Add("warnings:");
            lines.AddRange(model.HighRiskWarnings);
            return string.Join(Environment.NewLine, lines);
        }

    }

    private sealed class SmokeForegroundWindow : IConfirmationOverlayWindow
    {
        public IntPtr Handle { get; } = new(42);

        public FormWindowState WindowState { get; set; }

        public bool TopMost { get; set; }

        public string Text { get; set; } = "Codex Redaction Gate";

        public bool BroughtToFront { get; private set; }

        public bool Activated { get; private set; }

        public bool Focused { get; private set; }

        public void BringToFront()
        {
            BroughtToFront = true;
        }

        public void Activate()
        {
            Activated = true;
        }

        public bool Focus()
        {
            Focused = true;
            return true;
        }
    }

    private sealed class SmokeForegroundNativeMethods : IConfirmationOverlayNativeMethods
    {
        private readonly bool _foregroundActivated;

        public SmokeForegroundNativeMethods(bool foregroundActivated)
        {
            _foregroundActivated = foregroundActivated;
        }

        public bool ShowWindow(IntPtr hWnd, int command)
        {
            return true;
        }

        public bool SetForegroundWindow(IntPtr hWnd)
        {
            return _foregroundActivated;
        }

        public IntPtr GetForegroundWindow()
        {
            return new IntPtr(7);
        }

        public uint GetWindowThreadProcessId(IntPtr hWnd)
        {
            return 100;
        }

        public uint GetCurrentThreadId()
        {
            return 200;
        }

        public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach)
        {
            return true;
        }

        public bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
        {
            return true;
        }

        public bool BringWindowToTop(IntPtr hWnd)
        {
            return true;
        }

        public IntPtr SetActiveWindow(IntPtr hWnd)
        {
            return hWnd;
        }

        public IntPtr SetFocus(IntPtr hWnd)
        {
            return hWnd;
        }

        public bool FlashWindow(IntPtr hWnd, bool invert)
        {
            return true;
        }
    }
}

public sealed record ConfirmationOverlayForegroundActivationResult(
    bool ForegroundActivated,
    bool ActionRequiredStatusVisible,
    IReadOnlyList<string> RequestedCapabilities);

internal interface IConfirmationOverlayWindow
{
    IntPtr Handle { get; }

    FormWindowState WindowState { get; set; }

    bool TopMost { get; set; }

    string Text { get; set; }

    void BringToFront();

    void Activate();

    bool Focus();
}

internal interface IConfirmationOverlayNativeMethods
{
    bool ShowWindow(IntPtr hWnd, int command);

    bool SetForegroundWindow(IntPtr hWnd);

    IntPtr GetForegroundWindow();

    uint GetWindowThreadProcessId(IntPtr hWnd);

    uint GetCurrentThreadId();

    bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    bool BringWindowToTop(IntPtr hWnd);

    IntPtr SetActiveWindow(IntPtr hWnd);

    IntPtr SetFocus(IntPtr hWnd);

    bool FlashWindow(IntPtr hWnd, bool invert);
}

internal static class ConfirmationOverlayForegroundActivation
{
    public static ConfirmationOverlayForegroundActivationResult Request(
        IConfirmationOverlayWindow window,
        IConfirmationOverlayNativeMethods nativeMethods)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(nativeMethods);

        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }

        nativeMethods.ShowWindow(window.Handle, SwShow);
        nativeMethods.SetWindowPos(window.Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        window.TopMost = false;
        window.TopMost = true;
        window.BringToFront();
        window.Activate();
        window.Focus();
        var foregroundActivated = nativeMethods.SetForegroundWindow(window.Handle);
        if (!foregroundActivated)
        {
            foregroundActivated = TryAttachToForegroundThread(window.Handle, nativeMethods);
        }

        var actionRequiredStatusVisible = !foregroundActivated;
        if (actionRequiredStatusVisible)
        {
            window.Text = "Codex Redaction Gate - Action required";
        }

        nativeMethods.FlashWindow(window.Handle, invert: true);
        return new ConfirmationOverlayForegroundActivationResult(
            foregroundActivated,
            actionRequiredStatusVisible,
            WindowsConfirmationOverlay.ForegroundActivationRequestCapabilities);
    }

    private const int SwShow = 5;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new(-1);

    private static bool TryAttachToForegroundThread(IntPtr handle, IConfirmationOverlayNativeMethods nativeMethods)
    {
        var foreground = nativeMethods.GetForegroundWindow();
        var foregroundThreadId = foreground == IntPtr.Zero
            ? 0
            : nativeMethods.GetWindowThreadProcessId(foreground);
        var currentThreadId = nativeMethods.GetCurrentThreadId();
        if (foregroundThreadId == 0 || currentThreadId == 0 || foregroundThreadId == currentThreadId)
        {
            nativeMethods.BringWindowToTop(handle);
            nativeMethods.SetActiveWindow(handle);
            nativeMethods.SetFocus(handle);
            return nativeMethods.SetForegroundWindow(handle);
        }

        var attached = nativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, attach: true);
        try
        {
            nativeMethods.BringWindowToTop(handle);
            nativeMethods.SetActiveWindow(handle);
            nativeMethods.SetFocus(handle);
            nativeMethods.SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            return nativeMethods.SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                nativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, attach: false);
            }
        }
    }
}

internal sealed class Win32ConfirmationOverlayNativeMethods : IConfirmationOverlayNativeMethods
{
    public static Win32ConfirmationOverlayNativeMethods Instance { get; } = new();

    private Win32ConfirmationOverlayNativeMethods()
    {
    }

    public bool ShowWindow(IntPtr hWnd, int command)
    {
        return ShowWindowNative(hWnd, command);
    }

    public bool SetForegroundWindow(IntPtr hWnd)
    {
        return SetForegroundWindowNative(hWnd);
    }

    public IntPtr GetForegroundWindow()
    {
        return GetForegroundWindowNative();
    }

    public uint GetWindowThreadProcessId(IntPtr hWnd)
    {
        return GetWindowThreadProcessIdNative(hWnd, out _);
    }

    public uint GetCurrentThreadId()
    {
        return GetCurrentThreadIdNative();
    }

    public bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach)
    {
        return AttachThreadInputNative(idAttach, idAttachTo, attach);
    }

    public bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags)
    {
        return SetWindowPosNative(hWnd, hWndInsertAfter, x, y, cx, cy, flags);
    }

    public bool BringWindowToTop(IntPtr hWnd)
    {
        return BringWindowToTopNative(hWnd);
    }

    public IntPtr SetActiveWindow(IntPtr hWnd)
    {
        return SetActiveWindowNative(hWnd);
    }

    public IntPtr SetFocus(IntPtr hWnd)
    {
        return SetFocusNative(hWnd);
    }

    public bool FlashWindow(IntPtr hWnd, bool invert)
    {
        return FlashWindowNative(hWnd, invert);
    }

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool SetForegroundWindowNative(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr GetForegroundWindowNative();

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessIdNative(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadIdNative();

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput")]
    private static extern bool AttachThreadInputNative(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool SetWindowPosNative(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "BringWindowToTop")]
    private static extern bool BringWindowToTopNative(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetActiveWindow")]
    private static extern IntPtr SetActiveWindowNative(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetFocus")]
    private static extern IntPtr SetFocusNative(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "FlashWindow")]
    private static extern bool FlashWindowNative(IntPtr hWnd, bool invert);
}
