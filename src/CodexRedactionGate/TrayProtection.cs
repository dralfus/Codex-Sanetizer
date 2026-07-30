using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace CodexRedactionGate;

/// <summary>
/// Immutable resident protection state for one interception generation.
/// </summary>
internal sealed record ProtectionSnapshot(
    long Generation,
    TrayProtectionState State,
    Func<OsInteractionResult> ApplyOnlyRunner,
    NativeSubmitRuntimeSet? RuntimeSet,
    bool HookReady,
    ISendControlDiscovery? SendControlDiscovery,
    Func<TextSurfaceDiscoveryResult> ActiveSurfaceDiscovery);

internal sealed record NativeSubmitExecutionContext(
    ProtectionSnapshot Snapshot,
    NativeSubmitTargetIdentity? Target);

internal readonly record struct CapturedTargetProfileKey(IntPtr Window, uint ProcessId);

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
    bool SetupRequired = false,
    string ProgrammaticUiaInvokeStatus = OsInteractionStatusIds.ProgrammaticUiaInvokeUnsupported);

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
    private readonly NativeSubmitEnterprisePolicy _enterprisePolicy;
    private readonly DefaultStorageLayout _storageLayout;
    private readonly Func<IntPtr, string?> _selectedWindowProfileResolver;
    private readonly ConcurrentDictionary<CapturedTargetProfileKey, string> _capturedTargetProfiles = new();
    private readonly object _reloadGate = new();
    private readonly ConditionalWeakTable<NativeSubmitInterceptionResult, NativeSubmitExecutionContext> _classificationSnapshots = new();
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
        Func<TextSurfaceDiscoveryResult>? activeSurfaceDiscovery = null,
        Func<IntPtr, string?>? selectedWindowProfileResolver = null)
    {
        _hotkeyHost = hotkeyHost ?? throw new ArgumentNullException(nameof(hotkeyHost));
        _applyOnlyRunner = applyOnlyRunner ?? throw new ArgumentNullException(nameof(applyOnlyRunner));
        var resolvedProfile = nativeProfile ?? nativeSubmitController?.Profile;
        var runtimes = nativeSubmitRuntimes ?? CreateSingleRuntimeList(
            nativeSubmitHookHost,
            nativeSubmitController,
            nativeSubmitRunner,
            resolvedProfile);
        _enterprisePolicy = enterprisePolicy ?? NativeSubmitEnterprisePolicy.ConsumerDefault;
        _storageLayout = storageLayout ?? DefaultStorageLayout.CreateDefault();
        _selectedWindowProfileResolver = selectedWindowProfileResolver ?? WindowsSendControlDiscovery.TryGetSelectedProfileId;
        var surfaceDiscovery = activeSurfaceDiscovery ?? (() => TextSurfaceDiscoveryResult.Failure(
            OsInteractionStatusIds.NotComposer,
            new Dictionary<string, string>()));
        var state = CreateState(enabled: false, lastStatus: "disabled", runtimes: runtimes);
        _currentSnapshot = new ProtectionSnapshot(
            0,
            state,
            _applyOnlyRunner,
            nativeSubmitHookHost is null ? null : new NativeSubmitRuntimeSet(nativeSubmitHookHost, runtimes.ToArray()),
            HookReady: false,
            sendControlDiscovery,
            surfaceDiscovery);
    }

    public event EventHandler? StateChanged;

    public TrayProtectionState State => ReadSnapshot().State;

    internal bool IsNativeSubmitHookReady => ReadSnapshot().HookReady;

    public bool Start()
    {
        var snapshot = ReadSnapshot();
        var setupRequired = false;
        if (snapshot.RuntimeSet is not null)
        {
            try
            {
                setupRequired = IsAnySelectedProfileSetupRequired(snapshot.RuntimeSet);
            }
            catch
            {
                setupRequired = true;
            }
        }

        var manualHotkeyStarted = _hotkeyHost.Start(RunApplyOnlyOnce);
        var nativeStarted = snapshot.RuntimeSet is not null && StartNativeSubmitHook(snapshot.RuntimeSet);
        if (snapshot.RuntimeSet is not null && !nativeStarted)
        {
            if (manualHotkeyStarted)
            {
                _hotkeyHost.Stop();
            }

            PublishSnapshot(snapshot with
            {
                State = CreateState(
                    enabled: false,
                    lastStatus: NativeSubmitUnavailableStatus(snapshot),
                    runtimes: snapshot.RuntimeSet.Runtimes,
                    setupRequired: setupRequired)
            });
            return false;
        }

        if (!manualHotkeyStarted && !nativeStarted)
        {
            PublishSnapshot(snapshot with
            {
                State = CreateState(
                enabled: false,
                    lastStatus: _hotkeyHost.LastErrorCode ?? NativeSubmitUnavailableStatus(snapshot),
                    runtimes: snapshot.RuntimeSet?.Runtimes ?? Array.Empty<NativeSubmitRuntime>(),
                    setupRequired: setupRequired)
            });
            return false;
        }

        var nativeStatus = nativeStarted
            ? (setupRequired ? OsInteractionStatusIds.NativeSubmitSetupRequired : OsInteractionStatusIds.Protected)
            : NativeSubmitUnavailableStatus(snapshot);

        PublishSnapshot(snapshot with
        {
            State = CreateState(
                enabled: true,
                lastStatus: manualHotkeyStarted ? "enabled" : "enabled_native_submit_manual_hotkey_unavailable",
                runtimes: snapshot.RuntimeSet?.Runtimes ?? Array.Empty<NativeSubmitRuntime>(),
                nativeSubmitEnabled: nativeStarted && !setupRequired,
                nativeSubmitStatus: nativeStatus,
                setupRequired: setupRequired),
            HookReady = nativeStarted
        });
        return true;
    }

    public void Stop()
    {
        var snapshot = ReadSnapshot();
        snapshot.RuntimeSet?.HookHost.Stop();
        _hotkeyHost.Stop();
        PublishSnapshot(snapshot with
        {
            State = CreateState(enabled: false, lastStatus: "disabled", runtimes: Array.Empty<NativeSubmitRuntime>()),
            RuntimeSet = null,
            HookReady = false
        });
    }

    /// <summary>
    /// Replaces the native interception runtime after a binding verification.
    /// The candidate hook starts before its snapshot is published; the previous
    /// hook is stopped immediately after the new generation becomes active.
    /// </summary>
    public bool ReloadNativeSubmit(NativeSubmitRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return ReloadNativeSubmit(new NativeSubmitRuntimeSet(runtime.HookHost, new[] { runtime }));
    }

    public bool ReloadNativeSubmit(NativeSubmitRuntimeSet runtimeSet)
    {
        ArgumentNullException.ThrowIfNull(runtimeSet);
        lock (_reloadGate)
        {
            var previous = ReadSnapshot();
            return ReloadRuntime(previous, runtimeSet, previous.ApplyOnlyRunner);
        }
    }

    public bool ReloadResidentRuntime(ResidentProtectionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.NativeSubmitRuntimeSet is null)
        {
            return false;
        }

        lock (_reloadGate)
        {
            var previous = ReadSnapshot();
            return ReloadRuntime(previous, runtime.NativeSubmitRuntimeSet, runtime.ApplyOnlyRunner);
        }
    }

    private bool ReloadRuntime(
        ProtectionSnapshot previous,
        NativeSubmitRuntimeSet runtimeSet,
        Func<OsInteractionResult> applyOnlyRunner)
    {
        var candidate = TryBuildCandidateSnapshot(previous, runtimeSet, applyOnlyRunner);
        if (candidate is null)
        {
            return false;
        }

        if (!previous.State.Enabled)
        {
            previous.RuntimeSet?.HookHost.Stop();
            PublishSnapshot(candidate);
            return true;
        }

        if (!StartNativeSubmitHook(candidate.RuntimeSet!))
        {
            candidate.RuntimeSet!.HookHost.Stop();
            return false;
        }

        PublishSnapshot(candidate);
        if (previous.RuntimeSet is not null
            && !ReferenceEquals(previous.RuntimeSet.HookHost, candidate.RuntimeSet!.HookHost))
        {
            previous.RuntimeSet.HookHost.Stop();
        }

        return true;
    }

    /// <summary>
    /// Gets the current protection snapshot for reading
    /// This is an atomic, immutable view of all protection state
    /// </summary>
    public ProtectionSnapshot GetCurrentSnapshot()
    {
        return ReadSnapshot();
    }

    /// <summary>
    /// Builds a candidate snapshot from runtime set, validating all components
    /// Returns null if validation fails (retaining previous complete snapshot)
    /// </summary>
    private ProtectionSnapshot? TryBuildCandidateSnapshot(
        ProtectionSnapshot previous,
        NativeSubmitRuntimeSet runtimeSet,
        Func<OsInteractionResult> applyOnlyRunner)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(applyOnlyRunner);
            var runtimes = runtimeSet.Runtimes?.ToArray() ?? Array.Empty<NativeSubmitRuntime>();
            if (runtimeSet.HookHost is null
                || runtimes.Length == 0
                || runtimes.Any(runtime => runtime is null
                    || runtime.Controller is null
                    || runtime.Runner is null
                    || runtime.Profile is null
                    || string.IsNullOrWhiteSpace(runtime.Profile.ProfileId)))
            {
                return null;
            }

            var candidateRuntimeSet = new NativeSubmitRuntimeSet(runtimeSet.HookHost, runtimes);
            var setupRequired = IsAnySelectedProfileSetupRequired(candidateRuntimeSet);
            var state = CreateState(
                enabled: previous.State.Enabled,
                lastStatus: "native_submit_runtime_reloaded",
                runtimes: candidateRuntimeSet.Runtimes,
                nativeSubmitEnabled: previous.State.Enabled && !setupRequired,
                nativeSubmitStatus: previous.State.Enabled
                    ? (setupRequired ? OsInteractionStatusIds.NativeSubmitSetupRequired : OsInteractionStatusIds.Protected)
                    : OsInteractionStatusIds.NotConfigured,
                setupRequired: setupRequired);

            return new ProtectionSnapshot(
                previous.Generation + 1,
                state,
                applyOnlyRunner,
                candidateRuntimeSet,
                HookReady: previous.State.Enabled,
                previous.SendControlDiscovery,
                previous.ActiveSurfaceDiscovery);
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
        var snapshot = ReadSnapshot();
        var result = snapshot.ApplyOnlyRunner();
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
            NativeSubmitEnabled: snapshot.State.NativeSubmitEnabled,
            NativeSubmitStatus: snapshot.State.NativeSubmitStatus,
            ProtectedSendBinding: snapshot.State.ProtectedSendBinding,
            NewlineBinding: snapshot.State.NewlineBinding,
            ManualScanHotkey: snapshot.State.ManualScanHotkey,
            ReadinessStatus: snapshot.State.ReadinessStatus,
            ComposerProtected: snapshot.State.ComposerProtected,
            ProjectFilesProtected: snapshot.State.ProjectFilesProtected,
            ProjectFileStatus: snapshot.State.ProjectFileStatus,
            ResidentProcess: snapshot.State.ResidentProcess,
            SetupRequired: snapshot.State.SetupRequired);
        PublishSnapshotIfCurrent(snapshot, snapshot with { State = state });
    }

    private bool StartNativeSubmitHook(NativeSubmitRuntimeSet runtimeSet)
    {
        var keyboardStarted = runtimeSet.HookHost.Start(
            ClassifyNativeGesture,
            RunNativeSubmitOnce,
            ShouldSuppressKeyboardClassificationFailure);
        if (!keyboardStarted)
        {
            return false;
        }

        if (runtimeSet.HookHost is INativeSubmitPointerHookHost pointerHook
            && ReadSnapshot().SendControlDiscovery is not null)
        {
            if (!pointerHook.StartPointer(
                ClassifySendControl,
                RunNativeSendControlOnce,
                ShouldSuppressPointerClassificationFailure))
            {
                runtimeSet.HookHost.Stop();
                return false;
            }
        }

        return true;
    }

    private NativeSubmitInterceptionResult ClassifySendControl(NativePointerGesture gesture)
    {
        var snapshot = ReadSnapshot();
        if (snapshot.SendControlDiscovery is null || snapshot.RuntimeSet is null)
        {
            return RememberSnapshot(snapshot, PassThroughPointer(), target: null);
        }

        var discovery = snapshot.SendControlDiscovery.Discover(gesture);
        RememberCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId, discovery.ComposerDiscovery);
        var runtime = ResolveRuntime(snapshot, discovery.ComposerDiscovery)
            ?? ResolveRuntimeByProfileIdentity(snapshot, discovery.ComposerDiscovery);
        var result = discovery.Classification switch
        {
            SendControlClassification.IdentifiedSend when runtime is not null
                => runtime.Controller.HandleIdentifiedSendControl(discovery.ComposerDiscovery),
            SendControlClassification.SelectedClientUncertain when runtime is not null
                => SuppressUncertainSelectedSend(runtime.Profile.ProfileId),
            _ => PassThroughPointer()
        };
        return RememberSnapshot(
            snapshot,
            result,
            NativeSubmitTargetIdentity.TryCreate(snapshot.Generation, discovery.ComposerDiscovery.Surface));
    }

    private bool ShouldSuppressPointerClassificationFailure(NativePointerGesture gesture)
    {
        var snapshot = ReadSnapshot();
        if (gesture.TargetWindow == IntPtr.Zero)
        {
            return false;
        }

        var profileId = LookupCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId);
        return !string.IsNullOrWhiteSpace(profileId)
            && snapshot.RuntimeSet?.Runtimes.Any(runtime => string.Equals(
                runtime.Profile.ProfileId,
                profileId,
                StringComparison.Ordinal)) == true;
    }

    private bool ShouldSuppressKeyboardClassificationFailure(NativeKeyGesture gesture)
    {
        var snapshot = ReadSnapshot();
        if (gesture.TargetWindow == IntPtr.Zero)
        {
            return false;
        }

        var profileId = LookupCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId);
        var runtime = string.IsNullOrWhiteSpace(profileId)
            ? null
            : snapshot.RuntimeSet?.Runtimes.FirstOrDefault(candidate => string.Equals(
                candidate.Profile.ProfileId,
                profileId,
                StringComparison.Ordinal));
        return runtime?.Profile.SubmitBinding?.Matches(gesture) == true;
    }

    private string? LookupCapturedTargetProfile(IntPtr targetWindow, uint targetProcessId)
    {
        return TryCreateCapturedTargetProfileKey(targetWindow, targetProcessId, out var key)
            && _capturedTargetProfiles.TryGetValue(key, out var profileId)
            ? profileId
            : null;
    }

    private void RememberCapturedTargetProfile(
        IntPtr targetWindow,
        uint targetProcessId,
        TextSurfaceDiscoveryResult discovery)
    {
        if (!TryCreateCapturedTargetProfileKey(targetWindow, targetProcessId, out var key))
        {
            return;
        }

        var profileId = discovery.Surface?.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId)
            && !discovery.Diagnostics.TryGetValue("profile_id", out profileId))
        {
            try
            {
                profileId = _selectedWindowProfileResolver(targetWindow);
            }
            catch
            {
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(profileId))
        {
            _capturedTargetProfiles[key] = profileId;
        }
    }

    private static bool TryCreateCapturedTargetProfileKey(
        IntPtr targetWindow,
        uint targetProcessId,
        out CapturedTargetProfileKey key)
    {
        key = new CapturedTargetProfileKey(targetWindow, targetProcessId);
        return targetWindow != IntPtr.Zero && targetProcessId != 0;
    }

    private void RunNativeSendControlOnce(NativePointerGesture gesture, NativeSubmitInterceptionResult classification)
    {
        if (!TryTakeExecutionContext(classification, out var execution))
        {
            return;
        }

        var snapshot = execution.Snapshot;

        var runtime = ResolveClassifiedRuntime(snapshot, classification);
        if (runtime is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _nativeSubmitFlowInProgress, 1) == 1)
        {
            PublishNativeSubmitState(snapshot,
                OsInteractionStatusIds.NativeSubmitInProgress,
                OsInteractionStatusIds.Protected,
                runtime.Profile.ProfileId,
                applied: false,
                submitted: false);
            return;
        }

        try
        {
            var result = runtime.Controller.CompleteGuardedSubmit(
                classification,
                () => RunNativeSubmitFlow(runtime, execution.Target));
            PublishNativeSubmitState(snapshot,
                result.Status,
                NativeSubmitReadinessStatusAfterFlow(result.Status),
                runtime.Profile.ProfileId,
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

    private static NativeSubmitInterceptionResult SuppressUncertainSelectedSend(string profileId)
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.SurfaceUnverified,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["profile_id"] = profileId,
                ["send_control_status"] = "uncertain"
            });
    }

    private void RunNativeSubmitOnce(NativeKeyGesture gesture, NativeSubmitInterceptionResult classification)
    {
        if (!TryTakeExecutionContext(classification, out var execution))
        {
            return;
        }

        var snapshot = execution.Snapshot;

        var runtime = ResolveClassifiedRuntime(snapshot, classification);
        if (runtime is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _nativeSubmitFlowInProgress, 1) == 1)
        {
            PublishNativeSubmitState(snapshot,
                OsInteractionStatusIds.NativeSubmitInProgress,
                readinessStatus: OsInteractionStatusIds.Protected,
                profileId: runtime.Profile.ProfileId,
                applied: false,
                submitted: false);
            return;
        }

        try
        {
            var result = runtime.Controller.CompleteGuardedSubmit(
                classification,
                () => RunNativeSubmitFlow(runtime, execution.Target));

            var readinessStatus = NativeSubmitReadinessStatusAfterFlow(result.Status);
            var setupRequired = readinessStatus == OsInteractionStatusIds.NativeSubmitSetupRequired;
            PublishNativeSubmitState(snapshot,
                result.Status,
                readinessStatus,
                result.Diagnostics.TryGetValue("profile_id", out var profileId) ? profileId : runtime.Profile.ProfileId,
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
        ProtectionSnapshot eventSnapshot,
        string lastStatus,
        string readinessStatus,
        string? profileId,
        bool applied,
        bool submitted,
        bool setupRequired = false)
    {
        if (!ReferenceEquals(ReadSnapshot(), eventSnapshot))
        {
            return;
        }

        var state = new TrayProtectionState(
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
            ProtectedSendBinding: ProtectedSendBindingText(eventSnapshot, readinessStatus),
            NewlineBinding: NewlineBindingText(eventSnapshot),
            ManualScanHotkey: _hotkeyHost.Binding.DisplayText,
            ReadinessStatus: readinessStatus,
            ComposerProtected: readinessStatus == OsInteractionStatusIds.Protected,
            ProjectFilesProtected: false,
            ProjectFileStatus: ProjectFileProtectionStatusValues.NotConfigured,
            ResidentProcess: true);
        PublishSnapshot(eventSnapshot with { State = state });
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

    private static string NativeSubmitUnavailableStatus(ProtectionSnapshot snapshot)
    {
        if (snapshot.RuntimeSet is null || snapshot.RuntimeSet.Runtimes.Count == 0)
        {
            return OsInteractionStatusIds.NotConfigured;
        }

        return snapshot.RuntimeSet.HookHost.LastErrorCode ?? OsInteractionStatusIds.DegradedHotkeyOnly;
    }

    private TrayProtectionState CreateState(
        bool enabled,
        string lastStatus,
        IReadOnlyList<NativeSubmitRuntime> runtimes,
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
            ProtectedSendBinding: ProtectedSendBindingText(runtimes, nativeSubmitStatus),
            NewlineBinding: NewlineBindingText(runtimes),
            ManualScanHotkey: _hotkeyHost.Binding.DisplayText,
            ReadinessStatus: ReadinessStatus(runtimes, nativeSubmitStatus),
            ComposerProtected: nativeSubmitStatus == OsInteractionStatusIds.Protected,
            ProjectFilesProtected: false,
            ProjectFileStatus: ProjectFileProtectionStatusValues.NotConfigured,
            ResidentProcess: enabled,
            SetupRequired: setupRequired);
    }

    private bool EnterprisePolicyBlocksDisable()
    {
        var primaryRuntime = ReadSnapshot().RuntimeSet?.Runtimes.FirstOrDefault();
        return _enterprisePolicy.ManagedMode
            && primaryRuntime is not null
            && _enterprisePolicy.RequiredProfileIds.Contains(primaryRuntime.Profile.ProfileId, StringComparer.Ordinal);
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

    private static string ProtectedSendBindingText(
        ProtectionSnapshot snapshot,
        string nativeSubmitStatus)
    {
        return ProtectedSendBindingText(snapshot.RuntimeSet?.Runtimes ?? Array.Empty<NativeSubmitRuntime>(), nativeSubmitStatus);
    }

    private static string ProtectedSendBindingText(
        IReadOnlyList<NativeSubmitRuntime> runtimes,
        string nativeSubmitStatus)
    {
        var primaryRuntime = runtimes.FirstOrDefault();
        return nativeSubmitStatus == OsInteractionStatusIds.Protected && primaryRuntime?.Profile.SubmitBinding is not null
            ? primaryRuntime.Profile.SubmitBinding.DisplayText
            : "not_configured";
    }

    private static string NewlineBindingText(ProtectionSnapshot snapshot)
    {
        return NewlineBindingText(snapshot.RuntimeSet?.Runtimes ?? Array.Empty<NativeSubmitRuntime>());
    }

    private static string NewlineBindingText(IReadOnlyList<NativeSubmitRuntime> runtimes)
    {
        return runtimes.FirstOrDefault()?.Profile.NewlineBinding?.DisplayText ?? "unknown";
    }

    private static string ReadinessStatus(
        IReadOnlyList<NativeSubmitRuntime> runtimes,
        string nativeSubmitStatus)
    {
        var primaryRuntime = runtimes.FirstOrDefault();
        if (primaryRuntime is null)
        {
            return OsInteractionStatusIds.NotConfigured;
        }

        return nativeSubmitStatus == OsInteractionStatusIds.Protected
            ? OsInteractionStatusIds.Protected
            : primaryRuntime.Profile.CapabilityStatus;
    }

    private NativeSubmitInterceptionResult ClassifyNativeGesture(NativeKeyGesture gesture)
    {
        var snapshot = ReadSnapshot();
        var discovery = DiscoverActiveSurface(snapshot);
        RememberCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId, discovery);
        var focusedControlResult = ClassifyFocusedSendControl(snapshot, discovery, gesture, out var focusedComposerDiscovery);
        if (focusedControlResult is not null)
        {
            return RememberSnapshot(
                snapshot,
                focusedControlResult,
                NativeSubmitTargetIdentity.TryCreate(snapshot.Generation, focusedComposerDiscovery?.Surface));
        }

        var runtime = ResolveRuntime(snapshot, discovery);
        var knownDiscovery = discovery.Succeeded || HasProfileIdentity(discovery)
            ? discovery
            : null;
        runtime ??= ResolveRuntimeByProfileIdentity(snapshot, discovery);
        var result = runtime is null
            ? (snapshot.RuntimeSet?.Runtimes.Count > 1
                ? PassThroughPointer()
                : snapshot.RuntimeSet?.Runtimes.FirstOrDefault()?.Controller.HandleGesture(
                    gesture,
                    activeSurfaceDiscovery: knownDiscovery) ?? PassThroughPointer())
            : runtime.Controller.HandleGesture(gesture, activeSurfaceDiscovery: knownDiscovery);
        return RememberSnapshot(
            snapshot,
            result,
            NativeSubmitTargetIdentity.TryCreate(snapshot.Generation, discovery.Surface));
    }

    private NativeSubmitInterceptionResult? ClassifyFocusedSendControl(
        ProtectionSnapshot snapshot,
        TextSurfaceDiscoveryResult composerDiscovery,
        NativeKeyGesture gesture,
        out TextSurfaceDiscoveryResult? focusedComposerDiscovery)
    {
        focusedComposerDiscovery = null;
        if (composerDiscovery.Succeeded && composerDiscovery.Surface?.CanSubmit == true)
        {
            return null;
        }

        var sendControlDiscovery = snapshot.SendControlDiscovery;
        if (sendControlDiscovery is null)
        {
            return null;
        }

        var focusedControl = sendControlDiscovery.DiscoverFocusedControl(gesture.TargetWindow);
        focusedComposerDiscovery = focusedControl.ComposerDiscovery;
        RememberCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId, focusedComposerDiscovery);
        var runtime = ResolveRuntime(snapshot, focusedControl.ComposerDiscovery)
            ?? ResolveRuntimeByProfileIdentity(snapshot, focusedControl.ComposerDiscovery);
        if (runtime is not null && !IsFocusedSendActivation(runtime.Profile, gesture))
        {
            return null;
        }

        return focusedControl.Classification switch
        {
            SendControlClassification.IdentifiedSend when runtime is not null
                => runtime.Controller.HandleIdentifiedSendControl(focusedControl.ComposerDiscovery),
            SendControlClassification.SelectedClientUncertain when runtime is not null
                => SuppressUncertainSelectedSend(runtime.Profile.ProfileId),
            SendControlClassification.NonSendControl => PassThroughPointer(),
            _ => null
        };
    }

    private static TextSurfaceDiscoveryResult DiscoverActiveSurface(ProtectionSnapshot snapshot)
    {
        try
        {
            return snapshot.ActiveSurfaceDiscovery();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return TextSurfaceDiscoveryResult.Failure(
                OsInteractionStatusIds.SurfaceUnverified,
                new Dictionary<string, string>());
        }
    }

    private NativeSubmitRuntime? ResolveRuntime(
        ProtectionSnapshot snapshot,
        TextSurfaceDiscoveryResult? knownSurface = null)
    {
        if (snapshot.RuntimeSet is null)
        {
            return null;
        }

        if (knownSurface is not null)
        {
            if (!knownSurface.Succeeded || knownSurface.Surface is null)
            {
                return null;
            }

            return snapshot.RuntimeSet.Runtimes.FirstOrDefault(runtime => string.Equals(
                runtime.Profile.ProfileId,
                knownSurface.Surface.ProfileId,
                StringComparison.Ordinal));
        }

        if (snapshot.RuntimeSet.Runtimes.Count == 1)
        {
            return snapshot.RuntimeSet.Runtimes[0];
        }

        TextSurfaceDiscoveryResult discovery;
        try
        {
            discovery = snapshot.ActiveSurfaceDiscovery();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return null;
        }

        if (!discovery.Succeeded || discovery.Surface is null)
        {
            return null;
        }

        return snapshot.RuntimeSet.Runtimes.FirstOrDefault(runtime => string.Equals(
            runtime.Profile.ProfileId,
            discovery.Surface.ProfileId,
            StringComparison.Ordinal));
    }

    private static NativeSubmitRuntime? ResolveRuntimeByProfileIdentity(
        ProtectionSnapshot snapshot,
        TextSurfaceDiscoveryResult discovery)
    {
        var profileId = discovery.Surface?.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            discovery.Diagnostics.TryGetValue("profile_id", out profileId);
        }

        return string.IsNullOrWhiteSpace(profileId)
            ? null
            : snapshot.RuntimeSet?.Runtimes.FirstOrDefault(runtime => string.Equals(
                runtime.Profile.ProfileId,
                profileId,
                StringComparison.Ordinal));
    }

    private static bool HasProfileIdentity(TextSurfaceDiscoveryResult discovery)
    {
        return !string.IsNullOrWhiteSpace(discovery.Surface?.ProfileId)
            || discovery.Diagnostics.ContainsKey("profile_id");
    }

    private static bool IsFocusedSendActivation(SubmitBindingProfile profile, NativeKeyGesture gesture)
    {
        if (profile.SubmitBinding?.Matches(gesture) == true)
        {
            return true;
        }

        return !gesture.Ctrl
            && !gesture.Alt
            && !gesture.Shift
            && (string.Equals(gesture.Key, "Enter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(gesture.Key, "Space", StringComparison.OrdinalIgnoreCase));
    }

    private static NativeSubmitRuntime? ResolveClassifiedRuntime(
        ProtectionSnapshot snapshot,
        NativeSubmitInterceptionResult classification)
    {
        if (!classification.Diagnostics.TryGetValue("profile_id", out var profileId))
        {
            return null;
        }

        return snapshot.RuntimeSet?.Runtimes.FirstOrDefault(runtime => string.Equals(
            runtime.Profile.ProfileId,
            profileId,
            StringComparison.Ordinal));
    }

    private bool IsAnySelectedProfileSetupRequired(NativeSubmitRuntimeSet runtimeSet)
    {
        return runtimeSet.Runtimes.Any(runtime => runtime.Controller.IsSetupRequired(
            _storageLayout,
            runtime.Profile.ProfileId));
    }

    private ProtectionSnapshot ReadSnapshot()
    {
        return Volatile.Read(ref _currentSnapshot);
    }

    private void PublishSnapshot(ProtectionSnapshot snapshot)
    {
        Volatile.Write(ref _currentSnapshot, snapshot);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool PublishSnapshotIfCurrent(ProtectionSnapshot expected, ProtectionSnapshot replacement)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _currentSnapshot, replacement, expected), expected))
        {
            return false;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private NativeSubmitInterceptionResult RememberSnapshot(
        ProtectionSnapshot snapshot,
        NativeSubmitInterceptionResult classification,
        NativeSubmitTargetIdentity? target = null)
    {
        _classificationSnapshots.Add(classification, new NativeSubmitExecutionContext(snapshot, target));
        return classification;
    }

    private bool TryTakeExecutionContext(
        NativeSubmitInterceptionResult classification,
        out NativeSubmitExecutionContext execution)
    {
        if (_classificationSnapshots.TryGetValue(classification, out execution!))
        {
            _classificationSnapshots.Remove(classification);
            return true;
        }

        execution = null!;
        return false;
    }

    private static OsInteractionResult RunNativeSubmitFlow(
        NativeSubmitRuntime runtime,
        NativeSubmitTargetIdentity? target)
    {
        if (runtime.TargetRunner is null)
        {
            return runtime.Runner();
        }

        if (target is null || !string.Equals(target.ProfileId, runtime.Profile.ProfileId, StringComparison.Ordinal))
        {
            return new OsInteractionResult(
                OsInteractionStatusIds.StaleComposer,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>
                {
                    ["target_identity"] = "unavailable"
                });
        }

        return runtime.TargetRunner(target);
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
    SubmitBindingProfile Profile,
    Func<NativeSubmitTargetIdentity, OsInteractionResult>? TargetRunner = null);

internal sealed record NativeSubmitRuntimeSet(
    INativeSubmitHookHost HookHost,
    IReadOnlyList<NativeSubmitRuntime> Runtimes);

internal sealed record ResidentProtectionRuntime(
    Func<OsInteractionResult> ApplyOnlyRunner,
    NativeSubmitRuntimeSet? NativeSubmitRuntimeSet);

internal static class TrayStatusFormatter
{
    public static string FormatMenuStatus(TrayProtectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var enabled = state.Enabled ? "enabled" : "disabled";
        var replacements = state.LastReplacementCount is null
            ? "n/a"
            : state.LastReplacementCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"status={enabled} mode={state.Mode} composer_protected={state.ComposerProtected.ToString().ToLowerInvariant()} programmatic_uia_invoke={state.ProgrammaticUiaInvokeStatus} project_files_protected={state.ProjectFilesProtected.ToString().ToLowerInvariant()} project_file_status={state.ProjectFileStatus} protected_send_binding={state.ProtectedSendBinding} newline_binding={state.NewlineBinding} manual_scan_hotkey={state.ManualScanHotkey} native_submit={state.NativeSubmitStatus} readiness={state.ReadinessStatus} last={state.LastStatus} replacements={replacements}";
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

    public static string FormatRecoveryRequiredNotifyIconText(string localProtectionStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localProtectionStatus);

        return TrimNotifyText($"CodexRG local_protection={localProtectionStatus}");
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
