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
    RunLocalReadiness,
    RetryPromptProtection,
    RepairLocalProtection,
    RepairProfileSettings,
    CancelOperationalAction,
    RetryOperationalAction
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

        var rows = new List<LocalProtectionStatusRow>
        {
            CreateDpapiRow(trayState.LocalProtectionStatus),
            CreatePromptRow(trayState),
            CreateProjectFileRow(trayState.ProjectFilesProtected, trayState.ProjectFileStatus)
        };
        if (CreateOperationalActionRow(trayState) is { } operationalRow)
        {
            rows.Add(operationalRow);
        }

        return new LocalProtectionStatusView(rows);
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
        if (state.PromptProtectionRetryInProgress)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "retrying protection",
                "Protection runtime is being restarted; wait for the result before sending.",
                LocalProtectionStatusAction.None);
        }

        var chatGptReleaseCheckRequired = IsChatGptReleaseCheckRequired(state);
        if (state.LastProtectedSendInterruption is not null && !chatGptReleaseCheckRequired)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "previous Send interrupted",
                "The previous protected Send was interrupted while protection changed. Retry prompt protection.",
                LocalProtectionStatusAction.RetryPromptProtection);
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
                LocalProtectionStatusAction.RepairLocalProtection);
        }

        var setupProgressRow = CreateSetupVerificationRow(state);
        if (setupProgressRow is not null)
        {
            return setupProgressRow;
        }

        var localReadinessRow = CreateLocalReadinessRow(state);
        if (localReadinessRow is not null)
        {
            return localReadinessRow;
        }

        if (state.SetupRequired || state.NativeSubmitStatus == OsInteractionStatusIds.NativeSubmitSetupRequired)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "setup required",
                "Send from selected AI apps is blocked until profile verification succeeds.",
                LocalProtectionStatusAction.VerifyProfiles);
        }

        if (state.NativeSubmitStatus == OsInteractionStatusIds.ProfilesUnavailable
            || state.ReadinessStatus == OsInteractionStatusIds.ProfilesUnavailable)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "profile settings unavailable",
                "Protected Send remains blocked until the local profile settings can be read.",
                LocalProtectionStatusAction.RepairProfileSettings);
        }

        if (state.NativeSubmitStatus == OsInteractionStatusIds.SurfaceUnverified
            || state.ReadinessStatus == OsInteractionStatusIds.SurfaceUnverified)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "unsupported",
                "The selected ChatGPT Desktop surface no longer matches its verified fingerprint. Send is blocked; verify the app again.",
                LocalProtectionStatusAction.VerifyProfiles);
        }

        var attemptRow = CreateProtectedSendAttemptRow(
            state.ProtectedSendAttemptStatus,
            state.LastProtectedSendFailureCode);
        if (attemptRow is not null)
        {
            return attemptRow;
        }

        if (chatGptReleaseCheckRequired)
        {
            if (string.Equals(state.ReferenceAcceptanceStatus, "passed", StringComparison.Ordinal)
                && string.Equals(state.LiveContractStatus, "armed", StringComparison.Ordinal))
            {
                return new LocalProtectionStatusRow(
                    "Automatic prompt protection",
                    "Selected-app send interception",
                    "final ChatGPT check ready",
                    $"The local release check passed. {KeyboardHookNextAction(state.KeyboardHookStatus)} Send remains blocked until it records.",
                    LocalProtectionStatusAction.None);
            }

            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "degraded",
                $"Local readiness passed. Full reference acceptance ({state.ReferenceAcceptanceStatus}) and live contract ({state.LiveContractStatus}) are release/CI evidence, not a manual tray action. ChatGPT Desktop Send remains blocked.",
                LocalProtectionStatusAction.None);
        }

        if (state.LastProtectedSendInterruption is not null)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "previous Send interrupted",
                "The previous protected Send was interrupted while protection changed. Retry prompt protection.",
                LocalProtectionStatusAction.RetryPromptProtection);
        }

        if (state.Enabled && state.NativeSubmitEnabled && state.ComposerProtected)
        {
            return new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "keyboard Send active",
                $"Keyboard Send ({state.ProtectedSendBinding}) is checked before cloud submission. Clicking the app Send button is not protected until pointer pre-action verification is available.",
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

    private static bool IsChatGptReleaseCheckRequired(TrayProtectionState state)
    {
        return string.Equals(state.ConfiguredProfileId, "chatgpt-desktop", StringComparison.Ordinal)
            && !string.Equals(state.ProtectedClaimStatus, OsInteractionStatusIds.Protected, StringComparison.Ordinal);
    }

    private static LocalProtectionStatusRow? CreateSetupVerificationRow(TrayProtectionState state)
    {
        return state.SetupVerificationStatus switch
        {
            "waiting_for_focus" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "waiting for focus",
                "Focus the selected app composer to continue verification.", LocalProtectionStatusAction.None),
            "composer_recognized" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "composer recognized",
                "The selected app composer was recognized; binding verification is continuing.", LocalProtectionStatusAction.None),
            "verifying_binding" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "verifying Send key",
                "The selected Send key is being verified before cloud submission is allowed.", LocalProtectionStatusAction.None),
            "activating_protection" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "activating protection",
                "Verification succeeded; protected Send is being activated.", LocalProtectionStatusAction.None),
            "protected" => null,
            "unsupported_surface" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "setup needs focus",
                "Focus a Codex or ChatGPT Desktop message composer and verify prompt protection again.", LocalProtectionStatusAction.VerifyProfiles),
            "activation_failed" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "activation failed",
                "Protected Send remains blocked; restart prompt protection and try again.", LocalProtectionStatusAction.RetryPromptProtection),
            "setup_cancelled" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "setup not completed",
                "Protected Send remains blocked until prompt protection is verified.", LocalProtectionStatusAction.VerifyProfiles),
            "verification_failed" => new LocalProtectionStatusRow(
                "Automatic prompt protection", "Selected-app send interception", "verification failed",
                "Focus the selected app composer and verify prompt protection again.", LocalProtectionStatusAction.VerifyProfiles),
            _ => null
        };
    }

    private static LocalProtectionStatusRow? CreateLocalReadinessRow(TrayProtectionState state)
    {
        if (state.LocalReadinessStatus == "not_run"
            && !string.Equals(
                state.EffectiveOperationalAction.ActionKind,
                "local_readiness",
                StringComparison.Ordinal))
        {
            return null;
        }

        return state.LocalReadinessStatus switch
        {
            "checking" => new LocalProtectionStatusRow(
                "Automatic local readiness",
                "Resident prerequisite checks",
                $"checking {state.EffectiveOperationalAction.Stage}",
                "This check started automatically. Wait for the terminal result; protected Send remains fail-closed while it runs.",
                LocalProtectionStatusAction.None),
            "passed" => new LocalProtectionStatusRow(
                "Automatic local readiness",
                "Resident prerequisite checks",
                "completed",
                "The current resident readiness check completed. Protected Send is enabled only while its matching resident proof remains current.",
                LocalProtectionStatusAction.None),
            "cancelled" => new LocalProtectionStatusRow(
                "Automatic local readiness",
                "Resident prerequisite checks",
                "cancelled",
                "The local readiness check was cancelled. Protected Send remains blocked; retry the check.",
                LocalProtectionStatusAction.RunLocalReadiness),
            "failed" => new LocalProtectionStatusRow(
                "Automatic local readiness",
                "Resident prerequisite checks",
                "failed",
                "A local prerequisite failed. Protected Send remains blocked; retry the local readiness check after reviewing the raw-free status.",
                LocalProtectionStatusAction.RunLocalReadiness),
            "not_run" => new LocalProtectionStatusRow(
                "Automatic local readiness",
                "Resident prerequisite checks",
                "starting automatically",
                "The resident application will start the local readiness check automatically.",
                LocalProtectionStatusAction.None),
            _ => null
        };
    }

    private static LocalProtectionStatusRow? CreateOperationalActionRow(TrayProtectionState state)
    {
        var action = state.EffectiveOperationalAction;
        if (action.Status == "idle")
        {
            return null;
        }

        var status = action.Status == "running"
            ? $"running: {action.Stage}"
            : action.Status;
        var consequence = $"Action: {action.ActionKind}; input: {action.InputMode}; elapsed: {action.ElapsedMilliseconds} ms; next: {action.NextAction}.";
        var actionButton = action.Status == "running" && action.CanCancel
            ? LocalProtectionStatusAction.CancelOperationalAction
            : action.Status is "failed" or "cancelled"
                ? LocalProtectionStatusAction.RetryOperationalAction
                : LocalProtectionStatusAction.None;
        return new LocalProtectionStatusRow(
            "Current automatic action",
            "Resident operational lifecycle",
            status,
            consequence,
            actionButton);
    }

    private static LocalProtectionStatusRow? CreateProtectedSendAttemptRow(
        string status,
        string failureCode)
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
                "The previous protected Send is still in progress. Wait for it to finish.",
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
            "capture_failed" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: text capture failed",
                "The protected text capture failed; the original Send stayed blocked. Focus the composer and send again.",
                LocalProtectionStatusAction.None),
            "write_failed" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: replacement write failed",
                "The protected replacement could not be written; the original Send stayed blocked. Focus the composer and send again.",
                LocalProtectionStatusAction.None),
            "verification_failed" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: replacement verification failed",
                "The protected replacement could not be verified; the original Send stayed blocked. Focus the composer and send again.",
                LocalProtectionStatusAction.None),
            "submit_failed" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: protected replay failed",
                "The protected replay did not complete; the original Send stayed blocked. Focus the composer and send again.",
                LocalProtectionStatusAction.None),
            "replay_indeterminate" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: protected replay uncertain",
                "The protected replay could not be confirmed; the original Send stayed blocked. Focus the composer and send again.",
                LocalProtectionStatusAction.None),
            "binding_not_verified" or "setup_required" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked",
                "Verify prompt protection before sending.",
                LocalProtectionStatusAction.VerifyProfiles),
            "local_protection_unavailable" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: local protection unavailable",
                "Repair local protection before sending.",
                LocalProtectionStatusAction.RepairLocalProtection),
            "policy_blocked" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked by policy",
                "The original Send stayed blocked; contact the administrator.",
                LocalProtectionStatusAction.None),
            "protection_unavailable" when failureCode == "orchestrator_failure" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: local pipeline failed",
                "The local confirmation, write, or replay pipeline failed. The original Send stayed blocked; retry prompt protection before sending.",
                LocalProtectionStatusAction.RetryPromptProtection),
            "protection_unavailable" when failureCode == "native_submit_flow_failure" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: resident operation failed",
                "The resident protected Send operation failed. The original Send stayed blocked; retry prompt protection before sending.",
                LocalProtectionStatusAction.RetryPromptProtection),
            "protection_unavailable" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked: protection unavailable",
                "Retry prompt protection before sending.",
                LocalProtectionStatusAction.RetryPromptProtection),
            "trace_unavailable" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked",
                "The protected Send trace could not be completed. Retry prompt protection before sending.",
                LocalProtectionStatusAction.RetryPromptProtection),
            "content_blocked" => new LocalProtectionStatusRow(
                "Automatic prompt protection",
                "Selected-app send interception",
                "Send blocked by content policy",
                "Edit the prompt and send again.",
                LocalProtectionStatusAction.None),
            _ => null
        };
    }

    private static string KeyboardHookNextAction(string status)
    {
        return status switch
        {
            "configured_send_captured" => "Ctrl+Enter was captured; waiting for the protected Send result.",
            "enter_seen_binding_mismatch" => "Enter was seen in the selected app, but it did not match the configured Send binding. Verify the binding, then try again.",
            "enter_seen_unselected_target" => "Enter was seen outside the selected ChatGPT target. Focus the message composer, then send one non-sensitive message with Ctrl+Enter to complete the final check.",
            _ => "Focus the ChatGPT Desktop message composer, then send one non-sensitive message with Ctrl+Enter to complete the final check."
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
            LocalProtectionStatusAction.RunLocalReadiness => "Run local readiness check",
            LocalProtectionStatusAction.RetryPromptProtection => "Retry protection",
            LocalProtectionStatusAction.RepairLocalProtection => "Repair local protection",
            LocalProtectionStatusAction.RepairProfileSettings => "Open profile settings",
            LocalProtectionStatusAction.CancelOperationalAction => "Cancel action",
            LocalProtectionStatusAction.RetryOperationalAction => "Retry action",
            _ => string.Empty
        };
    }
}
