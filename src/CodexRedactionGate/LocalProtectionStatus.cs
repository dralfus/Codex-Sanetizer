using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal enum LocalProtectionStatusAction
{
    None,
    VerifyProfiles,
    RetryPromptProtection,
    RepairLocalProtection
}

internal sealed record LocalProtectionStatusRow(
    string Name,
    string Capability,
    string OperationalState,
    string Consequence,
    LocalProtectionStatusAction Action);

internal sealed record LocalProtectionStatusView(IReadOnlyList<LocalProtectionStatusRow> Rows)
{
    public static LocalProtectionStatusView Create(
        TrayProtectionState trayState)
    {
        ArgumentNullException.ThrowIfNull(trayState);

        return new LocalProtectionStatusView(new[]
        {
            CreateDpapiRow(trayState.LocalProtectionStatus),
            CreatePromptRow(trayState),
            CreateProjectFileRow(trayState.ProjectFilesProtected, trayState.ProjectFileStatus)
        });
    }

    public string RenderText()
    {
        return string.Join(
            Environment.NewLine,
            Rows.Select(row => $"{row.Name}: {row.Capability}; {row.OperationalState}; {row.Consequence}"));
    }

    private static LocalProtectionStatusRow CreateDpapiRow(string localProtectionStatus)
    {
        return localProtectionStatus switch
        {
            LocalProtectionRecovery.ReadyCode => new LocalProtectionStatusRow(
                "Local DPAPI protection",
                "DPAPI-backed local storage",
                "ready",
                "Local mappings are available to this Windows user.",
                LocalProtectionStatusAction.None),
            LocalProtectionRecovery.RecoveryRequiredCode => new LocalProtectionStatusRow(
                "Local DPAPI protection",
                "DPAPI-backed local storage",
                "recovery required",
                "Protected send is blocked until local protection is repaired.",
                LocalProtectionStatusAction.RepairLocalProtection),
            _ => new LocalProtectionStatusRow(
                "Local DPAPI protection",
                "DPAPI-backed local storage",
                "unavailable",
                "Protected send is unavailable until local protection is ready.",
                LocalProtectionStatusAction.None)
        };
    }

    private static LocalProtectionStatusRow CreatePromptRow(TrayProtectionState state)
    {
        if (state.SetupRequired || state.NativeSubmitStatus == OsInteractionStatusIds.NativeSubmitSetupRequired)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "setup required",
                "Send from selected AI apps is blocked until profile verification succeeds.",
                LocalProtectionStatusAction.VerifyProfiles);
        }

        if (!string.Equals(
                state.LocalProtectionStatus,
                LocalProtectionRecovery.ReadyCode,
                StringComparison.Ordinal))
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "unavailable",
                "Selected AI-app prompts remain blocked until local protection is ready.",
                LocalProtectionStatusAction.None);
        }

        if (state.Enabled && state.NativeSubmitEnabled && state.ComposerProtected)
        {
            var attemptRow = CreateProtectedSendAttemptRow(state.ProtectedSendAttemptStatus);
            if (attemptRow is not null)
            {
                return attemptRow;
            }

            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "active",
                "Verified selected AI-app prompts are checked before cloud submission.",
                LocalProtectionStatusAction.None);
        }

        if (state.Enabled)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "degraded",
                state.PromptProtectionRetryFailed
                    ? "Protection retry failed. Selected AI-app prompts remain unconfirmed and protected Send stays blocked."
                    : "Selected AI-app prompts are not confirmed as protected; retry protection before sending sensitive data.",
                LocalProtectionStatusAction.RetryPromptProtection);
        }

        return new LocalProtectionStatusRow(
            "Automatic prompt protection",
            "Selected-app send interception",
            "disabled",
            "Selected AI-app prompts are not intercepted while protection is stopped.",
            LocalProtectionStatusAction.None);
    }

    private static LocalProtectionStatusRow? CreateProtectedSendAttemptRow(string status)
    {
        return status switch
        {
            "detected" or "checking" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "checking Send",
                "The protected Send is being checked before cloud submission.",
                LocalProtectionStatusAction.None),
            "in_progress" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send in progress",
                "The previous protected Send is still in progress.",
                LocalProtectionStatusAction.None),
            "sent_safely" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "last Send protected",
                "The last protected Send completed without exposing its text in this status view.",
                LocalProtectionStatusAction.None),
            "canceled" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "last Send canceled",
                "The original Send stayed blocked. Edit the prompt or send it again.",
                LocalProtectionStatusAction.None),
            "composer_changed" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked",
                "Focus the original composer and send again.",
                LocalProtectionStatusAction.None),
            "verification_required" or "setup_required" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked",
                "Verify prompt protection before sending.",
                LocalProtectionStatusAction.VerifyProfiles),
            "send_blocked" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked",
                "The original Send stayed blocked. Check protection status, then send again.",
                LocalProtectionStatusAction.None),
            _ => null
        };
    }

    private static LocalProtectionStatusRow CreateProjectFileRow(bool liveProtected, string projectFileStatus)
    {
        if (liveProtected && projectFileStatus == ProjectFileProtectionStatusValues.Protected)
        {
            return new LocalProtectionStatusRow(
                "Project-file protection",
                "Project-file ingress protection",
                "live protected",
                "Protected project-file content is checked at the live ingress boundary.",
                LocalProtectionStatusAction.None);
        }

        return projectFileStatus switch
        {
            ProjectFileProtectionStatusValues.BrokerDemoOnly => new LocalProtectionStatusRow(
                "Project-file protection",
                "Project-file broker",
                "broker demo only",
                "Broker output can be sanitized locally, but live project-file ingress is unsupported and files are not intercepted before cloud submission.",
                LocalProtectionStatusAction.None),
            ProjectFileProtectionStatusValues.NotConfigured => new LocalProtectionStatusRow(
                "Project-file protection",
                "Project-file ingress protection",
                "not configured",
                "No live project-file protection is configured for cloud submission.",
                LocalProtectionStatusAction.None),
            _ => new LocalProtectionStatusRow(
                "Project-file protection",
                "Project-file ingress protection",
                "unsupported",
                "This project-file path is not protected at a supported live ingress boundary.",
                LocalProtectionStatusAction.None)
        };
    }
}

internal sealed class LocalProtectionStatusForm : Form
{
    private readonly Func<LocalProtectionStatusView> _viewFactory;
    private readonly Action<LocalProtectionStatusAction> _runAction;
    private readonly TableLayoutPanel _rows;
    private readonly Timer _refreshTimer;
    private bool _refreshTimerDisposed;

    internal IReadOnlyList<LocalProtectionStatusRow> CurrentRows { get; private set; } = Array.Empty<LocalProtectionStatusRow>();

    internal bool IsRefreshTimerDisposed => _refreshTimerDisposed;

    internal IReadOnlyList<Control> RowControls => _rows.Controls.Cast<Control>().ToArray();

    public LocalProtectionStatusForm(
        Func<LocalProtectionStatusView> viewFactory,
        Action<LocalProtectionStatusAction> runAction)
    {
        _viewFactory = viewFactory ?? throw new ArgumentNullException(nameof(viewFactory));
        _runAction = runAction ?? throw new ArgumentNullException(nameof(runAction));

        Text = "Code Sanitizer - Local protection status";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 320);
        Size = new Size(760, 410);

        _rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 0
        };
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(_rows);

        _refreshTimer = new Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshView();
        Shown += (_, _) => RefreshView();
        Activated += (_, _) => RefreshView();
        FormClosed += (_, _) => _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    internal void RefreshView()
    {
        if (HasSelectedStatusText())
        {
            return;
        }

        var view = _viewFactory();
        CurrentRows = view.Rows;
        _rows.SuspendLayout();
        try
        {
            foreach (Control control in _rows.Controls.Cast<Control>().ToArray())
            {
                control.Dispose();
            }

            _rows.Controls.Clear();
            _rows.RowStyles.Clear();
            _rows.RowCount = 0;

            foreach (var row in view.Rows)
            {
                AddRow(row);
            }
        }
        finally
        {
            _rows.ResumeLayout();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_refreshTimerDisposed)
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
            _refreshTimerDisposed = true;
        }

        base.Dispose(disposing);
    }

    private void AddRow(LocalProtectionStatusRow row)
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 0, 12)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            TabStop = true,
            ShortcutsEnabled = true,
            Dock = DockStyle.Fill,
            Height = 72,
            Text = $"{row.Name}{Environment.NewLine}Capability: {row.Capability}{Environment.NewLine}Status: {row.OperationalState}{Environment.NewLine}{row.Consequence}"
        };
        panel.Controls.Add(text, 0, 0);

        if (row.Action != LocalProtectionStatusAction.None)
        {
            var action = new Button
            {
                AutoSize = true,
                Text = ActionText(row.Action),
                Margin = new Padding(12, 0, 0, 0)
            };
            action.Click += (_, _) =>
            {
                _runAction(row.Action);
                RefreshView();
            };
            panel.Controls.Add(action, 1, 0);
        }

        _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rows.Controls.Add(panel, 0, _rows.RowCount++);
    }

    private bool HasSelectedStatusText()
    {
        return _rows.Controls
            .OfType<TableLayoutPanel>()
            .SelectMany(panel => panel.Controls.OfType<TextBox>())
            .Any(textBox => textBox.Focused || textBox.SelectionLength > 0);
    }

    private static string ActionText(LocalProtectionStatusAction action)
    {
        return action switch
        {
            LocalProtectionStatusAction.VerifyProfiles => "Set up prompt protection",
            LocalProtectionStatusAction.RetryPromptProtection => "Retry protection",
            LocalProtectionStatusAction.RepairLocalProtection => "Repair local protection",
            _ => string.Empty
        };
    }
}
