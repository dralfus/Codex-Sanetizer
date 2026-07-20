using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CodexRedactionGate;

public sealed class WindowsLiveTextSurfaceAdapter :
    ITextSurfaceReader,
    ITextSurfaceWriter,
    ISubmitAction
{
    public TextCaptureResult CaptureText(TextSurfaceDescriptor surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (!OperatingSystem.IsWindows())
        {
            return new TextCaptureResult(false, OsInteractionStatusIds.UnsupportedPlatform, null, new Dictionary<string, string>());
        }

        try
        {
            return RunSta(() =>
            {
                var clipboardBackup = ClipboardSnapshot.Capture();

                try
                {
                    RestoreForegroundWindow(surface);
                    SendKeys.SendWait("^a");
                    SendKeys.SendWait("^c");
                    WaitForUi();

                    var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                    return string.IsNullOrEmpty(text)
                        ? new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>())
                        : new TextCaptureResult(
                            true,
                            "captured",
                            text,
                            new Dictionary<string, string> { ["captured_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }
                finally
                {
                    clipboardBackup.Restore();
                }
            });
        }
        catch (ExternalException)
        {
            return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
        }
        catch (InvalidOperationException)
        {
            return new TextCaptureResult(false, OsInteractionStatusIds.CaptureFailed, null, new Dictionary<string, string>());
        }
    }

    public TextReplacementResult ReplaceText(TextSurfaceDescriptor surface, string text)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(text);

        if (!OperatingSystem.IsWindows())
        {
            return new TextReplacementResult(false, OsInteractionStatusIds.UnsupportedPlatform, new Dictionary<string, string>());
        }

        try
        {
            return RunSta(() =>
            {
                var clipboardBackup = ClipboardSnapshot.Capture();

                try
                {
                    RestoreForegroundWindow(surface);
                    Clipboard.SetText(text);
                    SendKeys.SendWait("^v");
                    WaitForUi();

                    return new TextReplacementResult(
                        true,
                        OsInteractionStatusIds.Applied,
                        new Dictionary<string, string> { ["write_length"] = text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }
                finally
                {
                    clipboardBackup.Restore();
                }
            });
        }
        catch (ExternalException)
        {
            return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
        }
        catch (InvalidOperationException)
        {
            return new TextReplacementResult(false, OsInteractionStatusIds.WriteFailed, new Dictionary<string, string>());
        }
    }

    public SubmitActionResult Submit(TextSurfaceDescriptor surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        if (!OperatingSystem.IsWindows())
        {
            return new SubmitActionResult(false, OsInteractionStatusIds.UnsupportedPlatform, new Dictionary<string, string>());
        }

        try
        {
            RestoreForegroundWindow(surface);
            SendKeys.SendWait("{ENTER}");
            WaitForUi();
            return new SubmitActionResult(true, OsInteractionStatusIds.Submitted, new Dictionary<string, string> { ["submit_key"] = "enter" });
        }
        catch (InvalidOperationException)
        {
            return new SubmitActionResult(false, OsInteractionStatusIds.SubmitFailed, new Dictionary<string, string>());
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
                ex is ExternalException
                || ex is InvalidOperationException)
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

    private static void RestoreForegroundWindow(TextSurfaceDescriptor surface)
    {
        if (surface.Metadata.TryGetValue("window_handle", out var rawHandle)
            && long.TryParse(rawHandle, System.Globalization.NumberStyles.HexNumber, null, out var handleValue)
            && handleValue != 0)
        {
            NativeMethods.SetForegroundWindow(new IntPtr(handleValue));
            WaitForUi();
        }
    }

    private static void WaitForUi()
    {
        Thread.Sleep(120);
        Application.DoEvents();
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

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
