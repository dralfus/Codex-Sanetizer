using System;

namespace CodexRedactionGate;

public sealed record TrayProtectionState(
    bool Enabled,
    string Mode,
    string Hotkey,
    string LastStatus,
    string? LastDecision,
    int? LastReplacementCount,
    string? LastProfileId,
    bool LastApplied,
    bool LastSubmitted);

internal interface ITrayHotkeyHost
{
    HotkeyBinding Binding { get; }

    string? LastErrorCode { get; }

    bool Start(Action onTriggered);

    void Stop();
}

internal sealed class TrayProtectionController
{
    private readonly ITrayHotkeyHost _hotkeyHost;
    private readonly Func<OsInteractionResult> _applyOnlyRunner;

    public TrayProtectionController(ITrayHotkeyHost hotkeyHost, Func<OsInteractionResult> applyOnlyRunner)
    {
        _hotkeyHost = hotkeyHost ?? throw new ArgumentNullException(nameof(hotkeyHost));
        _applyOnlyRunner = applyOnlyRunner ?? throw new ArgumentNullException(nameof(applyOnlyRunner));
        State = CreateState(enabled: false, lastStatus: "disabled");
    }

    public event EventHandler? StateChanged;

    public TrayProtectionState State { get; private set; }

    public bool Start()
    {
        if (!_hotkeyHost.Start(RunApplyOnlyOnce))
        {
            State = CreateState(
                enabled: false,
                lastStatus: _hotkeyHost.LastErrorCode ?? "hotkey_register_failed");
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        State = CreateState(enabled: true, lastStatus: "enabled");
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Stop()
    {
        _hotkeyHost.Stop();
        State = CreateState(enabled: false, lastStatus: "disabled");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunApplyOnlyOnce()
    {
        var result = _applyOnlyRunner();
        State = new TrayProtectionState(
            Enabled: true,
            Mode: "ApplyOnly",
            Hotkey: _hotkeyHost.Binding.DisplayText,
            LastStatus: result.Status,
            LastDecision: result.SanitizationResult is null
                ? null
                : CliOutputFormatting.FormatDecision(result.SanitizationResult.Decision),
            LastReplacementCount: result.SanitizationResult?.Replacements.Count,
            LastProfileId: result.Surface?.ProfileId,
            LastApplied: result.Applied,
            LastSubmitted: result.Submitted);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private TrayProtectionState CreateState(bool enabled, string lastStatus)
    {
        return new TrayProtectionState(
            Enabled: enabled,
            Mode: "ApplyOnly",
            Hotkey: _hotkeyHost.Binding.DisplayText,
            LastStatus: lastStatus,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: null,
            LastApplied: false,
            LastSubmitted: false);
    }
}

internal static class TrayStatusFormatter
{
    public static string FormatMenuStatus(TrayProtectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var enabled = state.Enabled ? "enabled" : "disabled";
        var replacements = state.LastReplacementCount is null
            ? "n/a"
            : state.LastReplacementCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"status={enabled} mode={state.Mode} hotkey={state.Hotkey} last={state.LastStatus} replacements={replacements}";
    }

    public static string FormatNotifyIconText(TrayProtectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var enabled = state.Enabled ? "enabled" : "disabled";
        return TrimNotifyText($"CodexRG {enabled} {state.Mode} {state.Hotkey} last={state.LastStatus}");
    }

    public static string FormatStartupError(TrayProtectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return $"Protection disabled. hotkey={state.Hotkey} error={state.LastStatus}";
    }

    private static string TrimNotifyText(string text)
    {
        return text.Length <= 63
            ? text
            : string.Concat(text.AsSpan(0, 60), "...");
    }
}

internal static class TrayMenuContent
{
    public static TrayLocalCommand DiagnosticsCommand { get; } = new("Diagnostics", "--policy-diagnostics");

    public static TrayLocalCommand AuditViewerCommand { get; } = new("Audit viewer", "--audit-view");

    public static TrayLocalCommand RuleManagementCommand { get; } = new("Rule management", "--dictionary-list");

    public static string RestoreText { get; } = string.Join(
        Environment.NewLine,
        "Local restore commands:",
        "--restore-view",
        "--restore-text \"sanitized model response\"");

    public static string DiagnosticsText { get; } = string.Join(
        Environment.NewLine,
        "Local diagnostics commands:",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --policy-diagnostics",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --audit-summary",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --audit-view",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --audit-verify",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --audit-cleanup --keep 100",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --os-compatibility-matrix",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --product-smoke",
        "dotnet run --project src/CodexRedactionGate/CodexRedactionGate.csproj -- --os-composer-diagnostic");

    public static string RuleManagementText { get; } = string.Join(
        Environment.NewLine,
        "Local rule management commands:",
        "--hotkey-show",
        "--hotkey-set \"Ctrl+Enter\"",
        "--send-mode-show",
        "--send-mode-enable",
        "--send-mode-disable",
        "--autostart-show",
        "--autostart-enable",
        "--autostart-disable",
        $"--local-data-cleanup [{LocalDataCleanup.ConfirmationFlag}]",
        "--dictionary-add-batch type value [type value]...",
        "--dictionary-list",
        "--dictionary-list --reveal",
        "--dictionary-import terms.csv",
        "--dictionary-remove id [id]...",
        "--policy-add-url-prefix prefix",
        "--policy-add-regex type pattern",
        "--policy-test \"text\" [--show-sanitized]",
        "--rules-export directory");
}

internal sealed record TrayLocalCommand(
    string Label,
    string CliArgument);
