using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace CodexRedactionGate;

/// <summary>
/// Immutable protection snapshot for a single generation
/// Contains all data needed to classify and guard a selected-profile submit
/// </summary>
/// <param name="Generation">Atomic snapshot generation number</param>
/// <param name="Mode">Protection mode</param>
/// <param name="Hotkey">Hotkey binding</param>
/// <param name="NativeSubmitEnabled">Whether native submit is enabled</param>
/// <param name="NativeSubmitStatus">Native submit status</param>
/// <param name="ProtectedSendBinding">Protected Send binding</param>
/// <param name="NewlineBinding">Newline binding</param>
/// <param name="ManualScanHotkey">Manual scan hotkey</param>
/// <param name="ReadinessStatus">Readiness status</param>
/// <param name="ComposerProtected">Whether composer is protected</param>
/// <param name="ProjectFilesProtected">Whether project files are protected</param>
/// <param name="ProjectFileStatus">Project file status</param>
/// <param name="ResidentProcess">Whether resident process</param>
/// <param name="SetupRequired">Whether setup is required</param>
/// <param name="HookHost">Native submit hook host</param>
/// <param name="Controller">Native submit controller</param>
/// <param name="Runner">Native submit runner</param>
/// <param name="Profile">Native submit profile</param>
/// <param name="Runtimes">All native submit runtimes</param>
internal sealed record ProtectionSnapshot(
    long Generation,
    string Mode,
    string Hotkey,
    string LastStatus,
    string? LastDecision,
    int? LastReplacementCount,
    string? LastProfileId,
    bool LastApplied,
    bool LastSubmitted,
    bool NativeSubmitEnabled,
    string NativeSubmitStatus,
    string ProtectedSendBinding,
    string NewlineBinding,
    string ManualScanHotkey,
    string ReadinessStatus,
    bool ComposerProtected,
    bool ProjectFilesProtected,
    string ProjectFileStatus,
    bool ResidentProcess,
    bool SetupRequired,
    INativeSubmitHookHost? HookHost,
    NativeSubmitInterceptionController? Controller,
    Func<OsInteractionResult>? Runner,
    SubmitBindingProfile? Profile,
    IReadOnlyList<NativeSubmitRuntime> Runtimes);

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
    private INativeSubmitHookHost? _nativeSubmitHookHost;
    private NativeSubmitInterceptionController? _nativeSubmitController;
    private Func<OsInteractionResult>? _nativeSubmitRunner;
    private SubmitBindingProfile? _nativeProfile;
    private IReadOnlyList<NativeSubmitRuntime> _nativeSubmitRuntimes;
    private readonly ISendControlDiscovery? _sendControlDiscovery;
    private readonly Func<TextSurfaceDiscoveryResult> _activeSurfaceDiscovery;
    private readonly NativeSubmitEnterprisePolicy _enterprisePolicy;
    private readonly DefaultStorageLayout _storageLayout;
    private int _nativeSubmitFlowInProgress;
    private ProtectionSnapshot _currentSnapshot;

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
        NativeSubmitEnterprisePolicy? enterprisePolicy = null,
        DefaultStorageLayout? storageLayout = null,
        ISendControlDiscovery? sendControlDiscovery = null,
        IReadOnlyList<NativeSubmitRuntime>? nativeSubmitRuntimes = null,
        Func<TextSurfaceDiscoveryResult>? activeSurfaceDiscovery = null)
    {
        _hotkeyHost = hotkeyHost ?? throw new ArgumentNullException(nameof(hotkeyHost));
        _applyOnlyRunner = applyOnlyRunner ?? throw new ArgumentNullException(nameof(applyOnlyRunner));
        _nativeSubmitHookHost = nativeSubmitHookHost;
        _nativeSubmitController = nativeSubmitController;
        _nativeSubmitRunner = nativeSubmitRunner;
        _nativeProfile = nativeProfile;
        _nativeSubmitRuntimes = nativeSubmitRuntimes ?? CreateSingleRuntimeList(
            nativeSubmitHookHost,
            nativeSubmitController,
            nativeSubmitRunner,
            nativeProfile);
        _enterprisePolicy = enterprisePolicy ?? NativeSubmitEnterprisePolicy.ConsumerDefault;
        _storageLayout = storageLayout ?? DefaultStorageLayout.CreateDefault();
        _sendControlDiscovery = sendControlDiscovery;
        _activeSurfaceDiscovery = activeSurfaceDiscovery ?? (() => WindowsActiveSurfaceDiscovery.CreateDefault().DiscoverActiveSurface());
        State = CreateState(enabled: false, lastStatus: "disabled");
        _currentSnapshot = CreateSnapshot(0, State, _nativeSubmitHookHost, _nativeSubmitController, _nativeSubmitRunner, _nativeProfile, _nativeSubmitRuntimes);
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
                setupRequired = IsAnySelectedProfileSetupRequired();
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
            var newState = CreateState(
                enabled: false,
                lastStatus: _hotkeyHost.LastErrorCode ?? NativeSubmitUnavailableStatus(),
                setupRequired: setupRequired);
            State = newState;
            _currentSnapshot = CreateSnapshot(0, newState, _nativeSubmitHookHost, _nativeSubmitController, _nativeSubmitRunner, _nativeProfile, _nativeSubmitRuntimes);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        var nativeStatus = nativeStarted
            ? (setupRequired ? OsInteractionStatusIds.NativeSubmitSetupRequired : OsInteractionStatusIds.Protected)
            : NativeSubmitUnavailableStatus();

        var newState2 = CreateState(
            enabled: true,
            lastStatus: manualHotkeyStarted ? "enabled" : "enabled_native_submit_manual_hotkey_unavailable",
            nativeSubmitEnabled: nativeStarted && !setupRequired,
            nativeSubmitStatus: nativeStatus,
            setupRequired: setupRequired);
        State = newState2;
        _currentSnapshot = CreateSnapshot(0, newState2, _nativeSubmitHookHost, _nativeSubmitController, _nativeSubmitRunner, _nativeProfile, _nativeSubmitRuntimes);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Stop()
    {
        _nativeSubmitHookHost?.Stop();
        _hotkeyHost.Stop();
        var state = CreateState(enabled: false, lastStatus: "disabled");
        State = state;
        _currentSnapshot = CreateSnapshot(0, state, null, null, null, null, Array.Empty<NativeSubmitRuntime>());
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the native interception runtime after a binding verification.
    /// The old hook is stopped before the new hook is registered, so a verified
    /// binding cannot leave two hook owners active.
    /// </summary>
    public bool ReloadNativeSubmit(NativeSubmitRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return ReloadNativeSubmit(new NativeSubmitRuntimeSet(runtime.HookHost, new[] { runtime }));
    }

    public bool ReloadNativeSubmit(NativeSubmitRuntimeSet runtimeSet)
    {
        ArgumentNullException.ThrowIfNull(runtimeSet);
        if (runtimeSet.Runtimes.Count == 0)
        {
            return false;
        }

        // Build and validate candidate before atomic publication
        var candidateSnapshot = TryBuildCandidateSnapshot(runtimeSet);
        if (candidateSnapshot is null)
        {
            // Validation failed - retain previous complete snapshot
            return false;
        }

        // Atomic publication: replace all fields together
        var previousHookHost = _nativeSubmitHookHost;
        var previousController = _nativeSubmitController;
        var previousRunner = _nativeSubmitRunner;
        var previousProfile = _nativeProfile;
        var previousRuntimes = _nativeSubmitRuntimes;
        var previousSnapshot = _currentSnapshot;

        _nativeSubmitHookHost = runtimeSet.HookHost;
        _nativeSubmitRuntimes = runtimeSet.Runtimes;
        var primaryRuntime = runtimeSet.Runtimes[0];
        _nativeSubmitController = primaryRuntime.Controller;
        _nativeSubmitRunner = primaryRuntime.Runner;
        _nativeProfile = primaryRuntime.Profile;

        if (!State.Enabled)
        {
            previousHookHost?.Stop();
            _currentSnapshot = candidateSnapshot;
            State = CreateState(enabled: false, lastStatus: "native_submit_runtime_reloaded");
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        var setupRequired = IsAnySelectedProfileSetupRequired();
        var started = StartNativeSubmitHook();
        if (!started)
        {
            // Rollback on failure - retain previous complete snapshot
            _nativeSubmitHookHost = previousHookHost;
            _nativeSubmitController = previousController;
            _nativeSubmitRunner = previousRunner;
            _nativeProfile = previousProfile;
            _nativeSubmitRuntimes = previousRuntimes;
            _currentSnapshot = previousSnapshot;
        }
        else if (previousHookHost is not null && !ReferenceEquals(previousHookHost, _nativeSubmitHookHost))
        {
            previousHookHost.Stop();
            _currentSnapshot = candidateSnapshot;
        }

        State = CreateState(
            enabled: true,
            lastStatus: started ? "native_submit_runtime_reloaded" : NativeSubmitUnavailableStatus(),
            nativeSubmitEnabled: started && !setupRequired,
            nativeSubmitStatus: started
                ? (setupRequired ? OsInteractionStatusIds.NativeSubmitSetupRequired : OsInteractionStatusIds.Protected)
                : NativeSubmitUnavailableStatus(),
            setupRequired: setupRequired);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return started;
    }

    /// <summary>
    /// Gets the current protection snapshot for reading
    /// This is an atomic, immutable view of all protection state
    /// </summary>
    public ProtectionSnapshot GetCurrentSnapshot()
    {
        return _currentSnapshot;
    }

    /// <summary>
    /// Builds a candidate snapshot from runtime set, validating all components
    /// Returns null if validation fails (retaining previous complete snapshot)
    /// </summary>
    private ProtectionSnapshot? TryBuildCandidateSnapshot(NativeSubmitRuntimeSet runtimeSet)
    {
        try
        {
            var nextGeneration = _currentSnapshot.Generation + 1;
            var state = CreateState(
                enabled: State.Enabled,
                lastStatus: "pending_reload",
                nativeSubmitEnabled: State.NativeSubmitEnabled,
                nativeSubmitStatus: State.NativeSubmitStatus,
                setupRequired: State.SetupRequired);

            return CreateSnapshot(
                nextGeneration,
                state,
                runtimeSet.HookHost,
                runtimeSet.Runtimes[0].Controller,
                runtimeSet.Runtimes[0].Runner,
                runtimeSet.Runtimes[0].Profile,
                runtimeSet.Runtimes);
        }
        catch
        {
            // Validation failed - return null to retain previous snapshot
            return null;
        }
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
        var state = new TrayProtectionState(
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
            ResidentProcess: State.ResidentProcess,
            SetupRequired: State.SetupRequired);
        State = state;
        // Snapshot is not updated for ApplyOnly - it's a transient state
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool StartNativeSubmitHook()
    {
        if (_nativeSubmitHookHost is null || _nativeSubmitController is null || _nativeSubmitRunner is null)
        {
            return false;
        }

        var keyboardStarted = _nativeSubmitHookHost.Start(
            ClassifyNativeGesture,
            RunNativeSubmitOnce);
        if (!keyboardStarted)
        {
            return false;
        }

        if (_nativeSubmitHookHost is INativeSubmitPointerHookHost pointerHook
            && _sendControlDiscovery is not null)
        {
            if (!pointerHook.StartPointer(
                ClassifySendControl,
                RunNativeSendControlOnce))
            {
                _nativeSubmitHookHost.Stop();
                return false;
            }
        }

        return true;
    }

    private NativeSubmitInterceptionResult ClassifySendControl(NativePointerGesture gesture)
    {
        if (_sendControlDiscovery is null || _nativeSubmitController is null)
        {
            return PassThroughPointer();
        }

        var discovery = _sendControlDiscovery.Discover(gesture);
        var runtime = ResolveRuntime(discovery.ComposerDiscovery);
        return discovery.Identified && runtime is not null
            ? runtime.Controller.HandleIdentifiedSendControl(discovery.ComposerDiscovery)
            : PassThroughPointer();
    }

    private void RunNativeSendControlOnce(NativePointerGesture gesture, NativeSubmitInterceptionResult classification)
    {
        var runtime = ResolveClassifiedRuntime(classification);
        if (runtime is null && (_nativeSubmitController is null || _nativeSubmitRunner is null))
        {
            return;
        }

        if (Interlocked.Exchange(ref _nativeSubmitFlowInProgress, 1) == 1)
        {
            PublishNativeSubmitState(
                OsInteractionStatusIds.NativeSubmitInProgress,
                OsInteractionStatusIds.Protected,
                _nativeProfile?.ProfileId,
                applied: false,
                submitted: false);
            return;
        }

        try
        {
            var result = runtime is not null
                ? runtime.Controller.CompleteGuardedSubmit(classification, runtime.Runner)
                : _nativeSubmitController!.CompleteGuardedSubmit(classification, _nativeSubmitRunner!);
            PublishNativeSubmitState(
                result.Status,
                NativeSubmitReadinessStatusAfterFlow(result.Status),
                runtime?.Profile.ProfileId ?? _nativeProfile?.ProfileId,
                result.Applied,
                result.Submitted);
        }
        finally
        {
            Volatile.Write(ref _nativeSubmitFlowInProgress, 0);
        }
    }

    private static NativeSubmitInterceptionResult PassThroughPointer()
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.NativeSubmitPassThrough,
            SuppressOriginalInput: false,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>());
    }

    private void RunNativeSubmitOnce(NativeKeyGesture gesture, NativeSubmitInterceptionResult classification)
    {
        var runtime = ResolveClassifiedRuntime(classification);
        if (runtime is null && (_nativeSubmitController is null || _nativeSubmitRunner is null))
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
            NativeSubmitInterceptionResult result = runtime is not null
                ? runtime.Controller.CompleteGuardedSubmit(classification, runtime.Runner)
                : _nativeSubmitController!.CompleteGuardedSubmit(classification, _nativeSubmitRunner!);

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

    private ProtectionSnapshot CreateSnapshot(
        long generation,
        TrayProtectionState state,
        INativeSubmitHookHost? hookHost,
        NativeSubmitInterceptionController? controller,
        Func<OsInteractionResult>? runner,
        SubmitBindingProfile? profile,
        IReadOnlyList<NativeSubmitRuntime> runtimes)
    {
        return new ProtectionSnapshot(
            Generation: generation,
            Mode: state.Mode,
            Hotkey: state.Hotkey,
            LastStatus: state.LastStatus,
            LastDecision: state.LastDecision,
            LastReplacementCount: state.LastReplacementCount,
            LastProfileId: state.LastProfileId,
            LastApplied: state.LastApplied,
            LastSubmitted: state.LastSubmitted,
            NativeSubmitEnabled: state.NativeSubmitEnabled,
            NativeSubmitStatus: state.NativeSubmitStatus,
            ProtectedSendBinding: state.ProtectedSendBinding,
            NewlineBinding: state.NewlineBinding,
            ManualScanHotkey: state.ManualScanHotkey,
            ReadinessStatus: state.ReadinessStatus,
            ComposerProtected: state.ComposerProtected,
            ProjectFilesProtected: state.ProjectFilesProtected,
            ProjectFileStatus: state.ProjectFileStatus,
            ResidentProcess: state.ResidentProcess,
            SetupRequired: state.SetupRequired,
            HookHost: hookHost,
            Controller: controller,
            Runner: runner,
            Profile: profile,
            Runtimes: runtimes);
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

    private NativeSubmitInterceptionResult ClassifyNativeGesture(NativeKeyGesture gesture)
    {
        var runtime = ResolveRuntime();
        return runtime is null
            ? (_nativeSubmitRuntimes.Count > 1
                ? PassThroughPointer()
                : _nativeSubmitController?.HandleGesture(gesture) ?? PassThroughPointer())
            : runtime.Controller.HandleGesture(gesture);
    }

    private NativeSubmitRuntime? ResolveRuntime(TextSurfaceDiscoveryResult? knownSurface = null)
    {
        if (_nativeSubmitRuntimes.Count == 1)
        {
            return _nativeSubmitRuntimes[0];
        }

        TextSurfaceDiscoveryResult discovery;
        try
        {
            discovery = knownSurface ?? _activeSurfaceDiscovery();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return null;
        }

        if (!discovery.Succeeded || discovery.Surface is null)
        {
            return null;
        }

        return _nativeSubmitRuntimes.FirstOrDefault(runtime => string.Equals(
            runtime.Profile.ProfileId,
            discovery.Surface.ProfileId,
            StringComparison.Ordinal));
    }

    private NativeSubmitRuntime? ResolveClassifiedRuntime(NativeSubmitInterceptionResult classification)
    {
        if (!classification.Diagnostics.TryGetValue("profile_id", out var profileId))
        {
            return null;
        }

        return _nativeSubmitRuntimes.FirstOrDefault(runtime => string.Equals(
            runtime.Profile.ProfileId,
            profileId,
            StringComparison.Ordinal));
    }

    private bool IsAnySelectedProfileSetupRequired()
    {
        return _nativeSubmitRuntimes.Any(runtime => runtime.Controller.IsSetupRequired(
            _storageLayout,
            runtime.Profile.ProfileId));
    }

    private static IReadOnlyList<NativeSubmitRuntime> CreateSingleRuntimeList(
        INativeSubmitHookHost? hookHost,
        NativeSubmitInterceptionController? controller,
        Func<OsInteractionResult>? runner,
        SubmitBindingProfile? profile)
    {
        return hookHost is not null && controller is not null && runner is not null && profile is not null
            ? new[] { new NativeSubmitRuntime(hookHost, controller, runner, profile) }
            : Array.Empty<NativeSubmitRuntime>();
    }
}

internal sealed record NativeSubmitRuntime(
    INativeSubmitHookHost HookHost,
    NativeSubmitInterceptionController Controller,
    Func<OsInteractionResult> Runner,
    SubmitBindingProfile Profile);

internal sealed record NativeSubmitRuntimeSet(
    INativeSubmitHookHost HookHost,
    IReadOnlyList<NativeSubmitRuntime> Runtimes);

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
