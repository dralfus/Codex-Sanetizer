using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

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
    bool LastSubmitted,
    bool NativeSubmitEnabled = false,
    string NativeSubmitStatus = OsInteractionStatusIds.NotConfigured,
    string ProtectedSendBinding = "not_configured",
    string NewlineBinding = "unknown",
    string ManualScanHotkey = "not_configured",
    string ReadinessStatus = OsInteractionStatusIds.NotConfigured,
    bool ComposerProtected = false,
    bool ProjectFilesProtected = false,
    string ProjectFileStatus = ProjectFileProtectionStatusValues.NotConfigured,
    bool ResidentProcess = false,
    bool SetupRequired = false);

public sealed record ProtectionDisableResult(
    bool Succeeded,
    string Code,
    bool ProtectionStillRunning,
    IReadOnlyDictionary<string, string> Diagnostics);

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
    private readonly INativeSubmitHookHost? _nativeSubmitHookHost;
    private readonly NativeSubmitInterceptionController? _nativeSubmitController;
    private readonly Func<OsInteractionResult>? _nativeSubmitRunner;
    private readonly SubmitBindingProfile? _nativeProfile;
    private readonly NativeSubmitEnterprisePolicy _enterprisePolicy;
    private int _nativeSubmitFlowInProgress;

    public TrayProtectionController(ITrayHotkeyHost hotkeyHost, Func<OsInteractionResult> applyOnlyRunner)
        : this(hotkeyHost, applyOnlyRunner, null, null, null)
    {
    }

    public TrayProtectionController(
        ITrayHotkeyHost hotkeyHost,
        Func<OsInteractionResult> applyOnlyRunner,
        INativeSubmitHookHost? nativeSubmitHookHost,
        NativeSubmitInterceptionController? nativeSubmitController,
        Func<OsInteractionResult>? nativeSubmitRunner,
        SubmitBindingProfile? nativeProfile = null,
        NativeSubmitEnterprisePolicy? enterprisePolicy = null)
    {
        _hotkeyHost = hotkeyHost ?? throw new ArgumentNullException(nameof(hotkeyHost));
        _applyOnlyRunner = applyOnlyRunner ?? throw new ArgumentNullException(nameof(applyOnlyRunner));
        _nativeSubmitHookHost = nativeSubmitHookHost;
        _nativeSubmitController = nativeSubmitController;
        _nativeSubmitRunner = nativeSubmitRunner;
        _nativeProfile = nativeProfile;
        _enterprisePolicy = enterprisePolicy ?? NativeSubmitEnterprisePolicy.ConsumerDefault;
        State = CreateState(enabled: false, lastStatus: "disabled");
    }

    public event EventHandler? StateChanged;

    public TrayProtectionState State { get; private set; }

    public bool Start()
    {
        // Check first-run setup status using public API (no reflection)
        var setupRequired = false;
        if (_nativeSubmitController is not null)
        {
            try
            {
                var setupLayout = DefaultStorageLayout.CreateDefault();
                setupRequired = _nativeSubmitController.IsSetupRequired(setupLayout, _nativeProfile?.ProfileId);
            }
            catch
            {
                // Ignore setup errors during startup - fail closed by treating as required
                setupRequired = true;
            }
        }

        var manualHotkeyStarted = _hotkeyHost.Start(RunApplyOnlyOnce);
        var nativeStarted = StartNativeSubmitHook();
        if (!manualHotkeyStarted && !nativeStarted)
        {
            State = CreateState(
                enabled: false,
                lastStatus: _hotkeyHost.LastErrorCode ?? NativeSubmitUnavailableStatus(),
                setupRequired: setupRequired);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        var nativeStatus = nativeStarted 
            ? (setupRequired ? OsInteractionStatusIds.NativeSubmitSetupRequired : OsInteractionStatusIds.Protected)
            : NativeSubmitUnavailableStatus();
            
        State = CreateState(
            enabled: true,
            lastStatus: manualHotkeyStarted ? "enabled" : "enabled_native_submit_manual_hotkey_unavailable",
            nativeSubmitEnabled: nativeStarted && !setupRequired,
            nativeSubmitStatus: nativeStatus,
            setupRequired: setupRequired);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Stop()
    {
        _nativeSubmitHookHost?.Stop();
        _hotkeyHost.Stop();
        State = CreateState(enabled: false, lastStatus: "disabled");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public ProtectionDisableResult TryDisableProtection(string action, bool confirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!confirmed)
        {
            return DisableResult(
                succeeded: false,
                code: "protection_disable_canceled",
                stillRunning: State.Enabled,
                action: action);
        }

        if (EnterprisePolicyBlocksDisable())
        {
            return DisableResult(
                succeeded: false,
                code: "protection_disable_blocked_by_policy",
                stillRunning: State.Enabled,
                action: action);
        }

        Stop();
        return DisableResult(
            succeeded: true,
            code: "protection_disable_confirmed",
            stillRunning: false,
            action: action);
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
            LastSubmitted: result.Submitted,
            NativeSubmitEnabled: State.NativeSubmitEnabled,
            NativeSubmitStatus: State.NativeSubmitStatus,
            ProtectedSendBinding: State.ProtectedSendBinding,
            NewlineBinding: State.NewlineBinding,
            ManualScanHotkey: State.ManualScanHotkey,
            ReadinessStatus: State.ReadinessStatus,
            ComposerProtected: State.ComposerProtected,
            ProjectFilesProtected: State.ProjectFilesProtected,
            ProjectFileStatus: State.ProjectFileStatus,
            ResidentProcess: State.ResidentProcess);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool StartNativeSubmitHook()
    {
        if (_nativeSubmitHookHost is null || _nativeSubmitController is null || _nativeSubmitRunner is null)
        {
            return false;
        }

        return _nativeSubmitHookHost.Start(
            gesture => _nativeSubmitController.HandleGesture(gesture),
            RunNativeSubmitOnce);
    }

    private void RunNativeSubmitOnce(NativeKeyGesture gesture)
    {
        if (_nativeSubmitController is null || _nativeSubmitRunner is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _nativeSubmitFlowInProgress, 1) == 1)
        {
            PublishNativeSubmitState(
                OsInteractionStatusIds.NativeSubmitInProgress,
                readinessStatus: OsInteractionStatusIds.Protected,
                profileId: _nativeProfile?.ProfileId,
                applied: false,
                submitted: false);
            return;
        }

        try
        {
            NativeSubmitInterceptionResult result = _nativeSubmitController.HandleGesture(gesture, _nativeSubmitRunner);

            var readinessStatus = NativeSubmitReadinessStatusAfterFlow(result.Status);
            var setupRequired = readinessStatus == OsInteractionStatusIds.NativeSubmitSetupRequired;
            PublishNativeSubmitState(
                result.Status,
                readinessStatus,
                result.Diagnostics.TryGetValue("profile_id", out var profileId) ? profileId : null,
                result.Applied,
                result.Submitted,
                setupRequired);
        }
        finally
        {
            Volatile.Write(ref _nativeSubmitFlowInProgress, 0);
        }
    }

    private void PublishNativeSubmitState(
        string lastStatus,
        string readinessStatus,
        string? profileId,
        bool applied,
        bool submitted,
        bool setupRequired = false)
    {
        State = new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: _hotkeyHost.Binding.DisplayText,
            LastStatus: lastStatus,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: profileId,
            LastApplied: applied,
            LastSubmitted: submitted,
            NativeSubmitEnabled: readinessStatus != OsInteractionStatusIds.DegradedHotkeyOnly
                && readinessStatus != OsInteractionStatusIds.NotConfigured,
            NativeSubmitStatus: readinessStatus,
            ProtectedSendBinding: ProtectedSendBindingText(readinessStatus),
            NewlineBinding: NewlineBindingText(),
            ManualScanHotkey: _hotkeyHost.Binding.DisplayText,
            ReadinessStatus: readinessStatus,
            ComposerProtected: readinessStatus == OsInteractionStatusIds.Protected,
            ProjectFilesProtected: false,
            ProjectFileStatus: ProjectFileProtectionStatusValues.NotConfigured,
            ResidentProcess: true);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string NativeSubmitReadinessStatusAfterFlow(string flowStatus)
    {
        return flowStatus is OsInteractionStatusIds.DegradedHotkeyOnly
            or OsInteractionStatusIds.EnterpriseBlocked
            or OsInteractionStatusIds.SurfaceUnverified
            or OsInteractionStatusIds.BindingUnknown
            or OsInteractionStatusIds.NotConfigured
            or OsInteractionStatusIds.NativeSubmitSetupRequired
            ? flowStatus
            : OsInteractionStatusIds.Protected;
    }

    private string NativeSubmitUnavailableStatus()
    {
        if (_nativeSubmitHookHost is null || _nativeSubmitController is null || _nativeSubmitRunner is null)
        {
            return OsInteractionStatusIds.NotConfigured;
        }

        return _nativeSubmitHookHost.LastErrorCode ?? OsInteractionStatusIds.DegradedHotkeyOnly;
    }

    private TrayProtectionState CreateState(
        bool enabled,
        string lastStatus,
        bool nativeSubmitEnabled = false,
        string nativeSubmitStatus = OsInteractionStatusIds.NotConfigured,
        bool setupRequired = false)
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
            LastSubmitted: false,
            NativeSubmitEnabled: nativeSubmitEnabled,
            NativeSubmitStatus: nativeSubmitStatus,
            ProtectedSendBinding: ProtectedSendBindingText(nativeSubmitStatus),
            NewlineBinding: NewlineBindingText(),
            ManualScanHotkey: _hotkeyHost.Binding.DisplayText,
            ReadinessStatus: ReadinessStatus(nativeSubmitStatus),
            ComposerProtected: nativeSubmitStatus == OsInteractionStatusIds.Protected,
            ProjectFilesProtected: false,
            ProjectFileStatus: ProjectFileProtectionStatusValues.NotConfigured,
            ResidentProcess: enabled,
            SetupRequired: setupRequired);
    }

    private bool EnterprisePolicyBlocksDisable()
    {
        return _enterprisePolicy.ManagedMode
            && _nativeProfile is not null
            && _enterprisePolicy.RequiredProfileIds.Contains(_nativeProfile.ProfileId, StringComparer.Ordinal);
    }

    private static ProtectionDisableResult DisableResult(
        bool succeeded,
        string code,
        bool stillRunning,
        string action)
    {
        return new ProtectionDisableResult(
            succeeded,
            code,
            stillRunning,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["action"] = action,
                ["selected_ai_apps_unprotected"] = succeeded.ToString().ToLowerInvariant(),
                ["raw_prompt_recorded"] = "false",
                ["audit_event"] = code
            });
    }

    private string ProtectedSendBindingText(string nativeSubmitStatus)
    {
        return nativeSubmitStatus == OsInteractionStatusIds.Protected && _nativeProfile?.SubmitBinding is not null
            ? _nativeProfile.SubmitBinding.DisplayText
            : "not_configured";
    }

    private string NewlineBindingText()
    {
        return _nativeProfile?.NewlineBinding?.DisplayText ?? "unknown";
    }

    private string ReadinessStatus(string nativeSubmitStatus)
    {
        if (_nativeProfile is null)
        {
            return OsInteractionStatusIds.NotConfigured;
        }

        return nativeSubmitStatus == OsInteractionStatusIds.Protected
            ? OsInteractionStatusIds.Protected
            : _nativeProfile.CapabilityStatus;
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
        return $"status={enabled} mode={state.Mode} composer_protected={state.ComposerProtected.ToString().ToLowerInvariant()} project_files_protected={state.ProjectFilesProtected.ToString().ToLowerInvariant()} project_file_status={state.ProjectFileStatus} protected_send_binding={state.ProtectedSendBinding} newline_binding={state.NewlineBinding} manual_scan_hotkey={state.ManualScanHotkey} native_submit={state.NativeSubmitStatus} readiness={state.ReadinessStatus} last={state.LastStatus} replacements={replacements}";
    }

    public static string FormatNotifyIconText(TrayProtectionState state, string? buildVersion = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var enabled = state.Enabled ? "enabled" : "disabled";
        var product = string.IsNullOrWhiteSpace(buildVersion)
            ? "CodexRG"
            : $"CodexRG {buildVersion}";
        return TrimNotifyText($"{product} {enabled} {state.Mode} last={state.LastStatus} send={state.ProtectedSendBinding} manual={state.ManualScanHotkey}");
    }

    public static string FormatStartupError(TrayProtectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return $"Protection disabled. manual_scan_hotkey={state.ManualScanHotkey} protected_send_binding={state.ProtectedSendBinding} readiness={state.ReadinessStatus} error={state.LastStatus}";
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

    public static TrayLocalCommand RuleManagementCommand { get; } = new("Sensitive terms", "--dictionary-ui");

    public static TrayLocalCommand VerifyCodexProfileCommand { get; } = new(
        "Verify Codex Desktop profile",
        "--native-profile-verify-delay codex-desktop Enter Ctrl+Enter 10");

    public static TrayLocalCommand VerifyChatGptProfileCommand { get; } = new(
        "Verify ChatGPT Desktop profile",
        "--native-profile-verify-delay chatgpt-desktop Enter Ctrl+Enter 10");

    public static string FormatBuildVersionMenuItem(string buildVersion)
    {
        return $"Version: {NormalizeBuildVersion(buildVersion)}";
    }

    public static string FormatBuildVersionHelpText(string buildVersion)
    {
        return string.Join(
            Environment.NewLine,
            "Build version:",
            NormalizeBuildVersion(buildVersion));
    }

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
        "Manual scan/apply hotkey commands:",
        "--hotkey-show",
        "--hotkey-set \"Ctrl+Shift+F9\"",
        "Protected Send binding commands:",
        "--native-profiles-status",
        "--native-profile-verify codex-desktop Enter Ctrl+Enter",
        "--native-profile-verify-delay codex-desktop Enter Ctrl+Enter 10",
        "--native-profile-verify-delay chatgpt-desktop Enter Ctrl+Enter 10",
        "Local sensitive terms UI:",
        "--dictionary-ui",
        "Local rule management CLI:",
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

    private static string NormalizeBuildVersion(string buildVersion)
    {
        return string.IsNullOrWhiteSpace(buildVersion)
            ? "unknown"
            : buildVersion.Trim();
    }
}

internal sealed record TrayLocalCommand(
    string Label,
    string CliArgument);

internal static class BuildVersion
{
    public static string Current => Resolve(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

    internal static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return Normalize(informationalVersion, assembly.GetName().Version);
    }

    internal static string Normalize(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        return assemblyVersion?.ToString() ?? "unknown";
    }
}
