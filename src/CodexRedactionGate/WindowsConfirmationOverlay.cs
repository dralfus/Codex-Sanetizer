using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexRedactionGate;

public sealed class WindowsConfirmationOverlay : IConfirmationOverlay
{
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

    private sealed class ConfirmationDialog : Form
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
            Shown += (_, _) => BringDialogToFront();

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
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            ShowWindow(Handle, SwShow);
            TopMost = true;
            BringToFront();
            Activate();
            SetForegroundWindow(Handle);
            FlashWindow(Handle, invert: true);
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

        private const int SwShow = 5;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool FlashWindow(IntPtr hWnd, bool invert);
    }
}
