using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal sealed class ResidentOverlayDispatchQueue : IDisposable
{
    private sealed class PendingRequest
    {
        public PendingRequest(
            ConfirmationUiModel model,
            Func<string, string, bool> traceStage)
        {
            Model = model;
            TraceStage = traceStage;
        }

        public ConfirmationUiModel Model { get; }

        public Func<string, string, bool> TraceStage { get; }

        public ConfirmationDecision? Decision { get; set; }

        public ManualResetEventSlim Completed { get; } = new(false);

        private int _cancelled;
        private int _completed;

        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;

        public void Cancel()
        {
            Interlocked.Exchange(ref _cancelled, 1);
        }

        public bool TryComplete(ConfirmationDecision decision)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return false;
            }

            Decision = decision;
            Completed.Set();
            return true;
        }
    }

    private readonly BlockingCollection<PendingRequest> _pending = new();
    private readonly Func<ConfirmationUiModel, Func<string, string, bool>, ConfirmationDecision> _handler;
    private readonly Action _cancelActive;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private PendingRequest? _activeRequest;
    private int _disposed;
    private int _cancelled;
    private int _uiThreadId;

    public ResidentOverlayDispatchQueue(
        Func<ConfirmationUiModel, Func<string, string, bool>, ConfirmationDecision> handler,
        Action cancelActive)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _cancelActive = cancelActive ?? throw new ArgumentNullException(nameof(cancelActive));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CodexRedactionGate.ConfirmationOverlay"
        };
        try
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }
        catch (PlatformNotSupportedException)
        {
        }

        _thread.Start();
    }

    public int UiThreadId => Volatile.Read(ref _uiThreadId);

    public ConfirmationDecision Request(
        ConfirmationUiModel model,
        Func<string, string, bool> traceStage)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(traceStage);

        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _cancelled) != 0)
        {
            return ConfirmationDecisionContract.Cancel(model);
        }

        if (Environment.CurrentManagedThreadId == UiThreadId)
        {
            return Execute(model, traceStage);
        }

        var request = new PendingRequest(model, traceStage);
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _cancelled) != 0)
            {
                return ConfirmationDecisionContract.Cancel(model);
            }

            try
            {
                _pending.Add(request);
            }
            catch (InvalidOperationException)
            {
                return ConfirmationDecisionContract.Cancel(model);
            }
        }

        request.Completed.Wait();
        return request.Decision ?? ConfirmationDecisionContract.Cancel(model);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _pending.CompleteAdding();
        }
        CancelPending();
        var joined = Environment.CurrentManagedThreadId == UiThreadId
            || _thread.Join(TimeSpan.FromSeconds(5));
        if (!joined)
        {
            return;
        }

        while (_pending.TryTake(out var request))
        {
            Complete(request, ConfirmationDecisionContract.Cancel(request.Model));
        }

        _pending.Dispose();
    }

    public void CancelPending()
    {
        Interlocked.Exchange(ref _cancelled, 1);
        lock (_gate)
        {
            foreach (var request in _pending)
            {
                CancelAndComplete(request);
            }

            if (_activeRequest is not null)
            {
                CancelAndComplete(_activeRequest);
            }
        }

        _cancelActive();
    }

    private void Run()
    {
        Volatile.Write(ref _uiThreadId, Environment.CurrentManagedThreadId);
        foreach (var request in _pending.GetConsumingEnumerable())
        {
            Volatile.Write(ref _activeRequest, request);
            try
            {
                var decision = Volatile.Read(ref _disposed) == 0
                    && Volatile.Read(ref _cancelled) == 0
                    && !request.IsCancelled
                    ? Execute(request.Model, request.TraceStage)
                    : ConfirmationDecisionContract.Cancel(request.Model);
                Complete(request, decision);
            }
            finally
            {
                Volatile.Write(ref _activeRequest, null);
            }
        }
    }

    private ConfirmationDecision Execute(
        ConfirmationUiModel model,
        Func<string, string, bool> traceStage)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return ConfirmationDecisionContract.Cancel(model);
            }

            return _handler(model, traceStage)
                ?? ConfirmationDecisionContract.Cancel(model);
        }
        catch
        {
            return ConfirmationDecisionContract.Cancel(model);
        }
    }

    private static void Complete(PendingRequest request, ConfirmationDecision decision)
    {
        request.TryComplete(decision);
    }

    private static void CancelAndComplete(PendingRequest request)
    {
        request.Cancel();
        Complete(request, ConfirmationDecisionContract.Cancel(request.Model));
    }
}

public sealed class WindowsConfirmationOverlay : ITracedConfirmationOverlay, IDisposable
{
    private readonly ResidentOverlayDispatchQueue? _dispatcher;
    private readonly Action<ConfirmationOverlayAcceptanceWindow>? _acceptanceAutomation;
    private ConfirmationDialog? _activeDialog;

    public WindowsConfirmationOverlay()
        : this(null)
    {
    }

    // This is intentionally internal: the reference composer uses the real dialog
    // and foreground path, then drives its visible decision deterministically.
    internal WindowsConfirmationOverlay(Action<ConfirmationOverlayAcceptanceWindow>? acceptanceAutomation)
    {
        _acceptanceAutomation = acceptanceAutomation;
        if (OperatingSystem.IsWindows())
        {
            _dispatcher = new ResidentOverlayDispatchQueue(ShowConfirmation, CloseActiveDialog);
        }
    }

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
        return RequestConfirmation(model, static (_, _) => true);
    }

    public ConfirmationDecision RequestConfirmation(
        ConfirmationUiModel model,
        Func<string, string, bool> traceStage)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(traceStage);

        if (!OperatingSystem.IsWindows())
        {
            return ConfirmationDecisionContract.Cancel(model);
        }

        return _dispatcher?.Request(model, traceStage)
            ?? ConfirmationDecisionContract.Cancel(model);
    }

    public void Dispose()
    {
        _dispatcher?.Dispose();
    }

    internal void CancelActiveConfirmation()
    {
        _dispatcher?.CancelPending();
    }

    private ConfirmationDecision ShowConfirmation(
        ConfirmationUiModel model,
        Func<string, string, bool> traceStage)
    {
        ThreadExceptionEventHandler? threadExceptionHandler = null;
        Exception? dialogException = null;
        ConfirmationDialog? dialog = null;
        try
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            }
            catch (InvalidOperationException)
            {
            }

            threadExceptionHandler = (_, exceptionEvent) =>
            {
                dialogException = exceptionEvent.Exception;
                CloseDialog(dialog);
            };
            Application.ThreadException += threadExceptionHandler;

            using var ownedDialog = new ConfirmationDialog(model, traceStage, _acceptanceAutomation);
            dialog = ownedDialog;
            Volatile.Write(ref _activeDialog, dialog);
            if (dialog.CloseRequested)
            {
                return ConfirmationDecisionContract.Cancel(model);
            }

            var result = dialog.ShowDialog();
            return dialogException is null && result == DialogResult.OK
                ? new ConfirmationDecision(true, new ApprovedSanitizedPayload(dialog.EditedSanitizedText))
                : ConfirmationDecisionContract.Cancel(model);
        }
        catch
        {
            return ConfirmationDecisionContract.Cancel(model);
        }
        finally
        {
            Volatile.Write(ref _activeDialog, null);
            if (threadExceptionHandler is not null)
            {
                Application.ThreadException -= threadExceptionHandler;
            }
        }
    }

    private void CloseActiveDialog()
    {
        CloseDialog(Volatile.Read(ref _activeDialog));
    }

    private static void CloseDialog(ConfirmationDialog? dialog)
    {
        if (dialog is null)
        {
            return;
        }

        try
        {
            dialog.RequestClose();
        }
        catch
        {
            // Disposal is fail-closed; the dispatcher also converts the request to Cancel.
        }
    }

    private sealed class ConfirmationDialog : Form, IConfirmationOverlayWindow
    {
        private readonly RichTextBox _promptBox;

        private readonly Func<string, string, bool> _traceStage;
        private bool _foregroundTracePublished;
        private int _closeRequested;

        private readonly Action<ConfirmationOverlayAcceptanceWindow>? _acceptanceAutomation;

        public ConfirmationDialog(
            ConfirmationUiModel model,
            Func<string, string, bool> traceStage,
            Action<ConfirmationOverlayAcceptanceWindow>? acceptanceAutomation)
        {
            _traceStage = traceStage;
            _acceptanceAutomation = acceptanceAutomation;
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

            _promptBox = new RichTextBox
            {
                ReadOnly = false,
                Dock = DockStyle.Fill,
                Text = model.SanitizedPrompt,
                Font = new Font(FontFamily.GenericMonospace, 10),
                DetectUrls = false
            };
            Highlight(_promptBox, model);

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
            Controls.Add(_promptBox);
            Controls.Add(details);
            Controls.Add(buttons);
            AcceptButton = confirm;
            CancelButton = cancel;
        }

        public string EditedSanitizedText => _promptBox.Text;

        public bool CloseRequested => Volatile.Read(ref _closeRequested) != 0;

        public void RequestClose()
        {
            Interlocked.Exchange(ref _closeRequested, 1);
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            void Close()
            {
                if (!IsDisposed)
                {
                    DialogResult = DialogResult.Cancel;
                    base.Close();
                }
            }

            if (InvokeRequired)
            {
                BeginInvoke((Action)Close);
            }
            else
            {
                Close();
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (CloseRequested)
            {
                RequestClose();
            }
        }

        private void BringDialogToFront()
        {
            var result = ConfirmationOverlayForegroundActivation.Request(this, Win32ConfirmationOverlayNativeMethods.Instance);
            if (result.ForegroundActivated && !_foregroundTracePublished)
            {
                _foregroundTracePublished = _traceStage(
                    "overlay_foreground_confirmed",
                    "foreground_verified");
                if (!_foregroundTracePublished)
                {
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            }

            if (result.ForegroundActivated && _acceptanceAutomation is not null)
            {
                _acceptanceAutomation(new ConfirmationOverlayAcceptanceWindow(
                    () => BeginInvoke(new Action(() => DialogResult = DialogResult.OK)),
                    () => BeginInvoke(new Action(() => DialogResult = DialogResult.Cancel))));
            }
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

internal sealed class ConfirmationOverlayAcceptanceWindow
{
    private readonly Action _approve;
    private readonly Action _cancel;

    public ConfirmationOverlayAcceptanceWindow(Action approve, Action cancel)
    {
        _approve = approve ?? throw new ArgumentNullException(nameof(approve));
        _cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
    }

    public void Approve() => _approve();

    public void Cancel() => _cancel();
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
