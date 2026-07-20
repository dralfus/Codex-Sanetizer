using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal sealed class LocalRestoreForm : Form
{
    private readonly LocalRestoreWorkflow _workflow;
    private readonly TextBox _inputTextBox;
    private readonly TextBox _outputTextBox;
    private readonly Label _statusLabel;

    public LocalRestoreForm(LocalRestoreWorkflow workflow)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

        Text = "Codex Redaction Gate - Local restore";
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 5,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _inputTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false
        };
        _outputTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            ReadOnly = true
        };
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text = "LOCAL RESTORE VIEW"
        };
        var restoreButton = new Button
        {
            Text = "Restore locally",
            AutoSize = true
        };
        restoreButton.Click += (_, _) => Restore();

        root.Controls.Add(new Label { Text = "Sanitized response", AutoSize = true, Dock = DockStyle.Fill }, 0, 0);
        root.Controls.Add(_inputTextBox, 0, 1);
        root.Controls.Add(restoreButton, 0, 2);
        root.Controls.Add(_outputTextBox, 0, 3);
        root.Controls.Add(_statusLabel, 0, 4);

        Controls.Add(root);
    }

    private void Restore()
    {
        try
        {
            var result = _workflow.RestoreText(_inputTextBox.Text);
            _outputTextBox.Text = result.DisplayText;
            _statusLabel.Text = result.Restoration.Metadata.LocalSensitive
                ? "LOCAL-SENSITIVE restored output is shown only in this local window."
                : "No local-sensitive values were restored.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                exception.Message,
                "Codex Redaction Gate - Local restore failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
