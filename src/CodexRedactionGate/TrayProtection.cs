using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    NativeSubmitRuntimeSet RuntimeSet,
    NativeSubmitTargetIdentity? Target);

internal readonly record struct CapturedTargetProfileKey(IntPtr Window, uint ProcessId);

internal readonly record struct SetupReadiness(
    bool SetupRequired,
    string Status);

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
    string ProgrammaticUiaInvokeStatus = OsInteractionStatusIds.ProgrammaticUiaInvokeUnsupported,
    string LocalProtectionStatus = LocalProtectionRecovery.ReadyCode,
    bool PromptProtectionRetryFailed = false,
    string? ConfiguredProfileId = null,
    string ProtectedSendAttemptStatus = "idle",
    string ProtectedSendAttemptAction = "none",
    long ProtectedSendAttemptId = 0,
    IReadOnlyList<ProtectedSendTraceEntry>? ProtectedSendAttemptTrace = null,
    long ProtectedSendAttemptStartedAtTimestamp = 0,
    ProtectedSendInterruption? LastProtectedSendInterruption = null,
    string LastProtectedSendTraceStatus = "none",
    string SetupVerificationStatus = "idle",
    string SetupVerificationAction = "none",
    string? SetupVerificationProfileId = null,
    string SetupVerificationBinding = "not_configured",
    long SetupVerificationAttemptId = 0,
    string ProtectedClaimStatus = OsInteractionStatusIds.NotConfigured,
    string ReferenceAcceptanceStatus = "not_applicable",
    string LiveContractStatus = "not_applicable");

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
    private readonly Action<string>? _protectedSendStageObserver;
    private readonly Action? _beforeProtectedSendTracePublishForTesting;
    private Action<ProtectedSendTraceEntry>? _protectedSendTracePublishedForTesting;
    private IDisposable? _residentRuntimeOwner;
    private readonly ConcurrentDictionary<CapturedTargetProfileKey, string> _capturedTargetProfiles = new();
    private readonly object _reloadGate = new();
    private readonly ConditionalWeakTable<NativeSubmitInterceptionResult, NativeSubmitExecutionContext> _classificationSnapshots = new();
    private ResidentProtectedSendOperation? _activeProtectedSendOperation;
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
        SubmitBindingProfile? nativeProfile = null,
        NativeSubmitEnterprisePolicy? enterprisePolicy = null,
        DefaultStorageLayout? storageLayout = null,
        ISendControlDiscovery? sendControlDiscovery = null,
        IReadOnlyList<NativeSubmitRuntime>? nativeSubmitRuntimes = null,
        Func<TextSurfaceDiscoveryResult>? activeSurfaceDiscovery = null,
        Func<IntPtr, string?>? selectedWindowProfileResolver = null,
        Action<string>? protectedSendStageObserver = null,
        IDisposable? residentRuntimeOwner = null,
        IDisposable? nativeSubmitRuntimeOwner = null,
        Action? beforeProtectedSendTracePublishForTesting = null)
    {
        _hotkeyHost = hotkeyHost ?? throw new ArgumentNullException(nameof(hotkeyHost));
        _applyOnlyRunner = applyOnlyRunner ?? throw new ArgumentNullException(nameof(applyOnlyRunner));
        var resolvedProfile = nativeProfile ?? nativeSubmitController?.Profile;
        var runtimes = nativeSubmitRuntimes ?? Array.Empty<NativeSubmitRuntime>();
        _enterprisePolicy = enterprisePolicy ?? NativeSubmitEnterprisePolicy.ConsumerDefault;
        _storageLayout = storageLayout ?? DefaultStorageLayout.CreateDefault();
        _selectedWindowProfileResolver = selectedWindowProfileResolver ?? WindowsSendControlDiscovery.TryGetSelectedProfileId;
        _protectedSendStageObserver = protectedSendStageObserver;
        _beforeProtectedSendTracePublishForTesting = beforeProtectedSendTracePublishForTesting;
        _residentRuntimeOwner = residentRuntimeOwner;
        var surfaceDiscovery = activeSurfaceDiscovery ?? (() => TextSurfaceDiscoveryResult.Failure(
            OsInteractionStatusIds.NotComposer,
            new Dictionary<string, string>()));
        var state = CreateState(enabled: false, lastStatus: "disabled", runtimes: runtimes);
        _currentSnapshot = new ProtectionSnapshot(
            0,
            state,
            _applyOnlyRunner,
            nativeSubmitHookHost is null
                ? null
                : new NativeSubmitRuntimeSet(nativeSubmitHookHost, runtimes.ToArray(), nativeSubmitRuntimeOwner),
            HookReady: false,
            sendControlDiscovery,
            surfaceDiscovery);
        nativeSubmitController?.SetResidentProtectedClaimProvider(ReadResidentChatGptClaim);
    }

    // Explicit test seam for controller tests that do not construct the Windows orchestrator.
    internal static TrayProtectionController CreateTest(
        ITrayHotkeyHost hotkeyHost,
        Func<OsInteractionResult> applyOnlyRunner)
    {
        return new TrayProtectionController(hotkeyHost, applyOnlyRunner);
    }

    // Explicit test seam for tests that already own traced runtime fixtures.
    internal static TrayProtectionController CreateTest(
        ITrayHotkeyHost hotkeyHost,
        Func<OsInteractionResult> applyOnlyRunner,
        INativeSubmitHookHost nativeSubmitHookHost,
        NativeSubmitInterceptionController nativeSubmitController,
        SubmitBindingProfile nativeProfile,
        NativeSubmitEnterprisePolicy? enterprisePolicy = null,
        DefaultStorageLayout? storageLayout = null,
        ISendControlDiscovery? sendControlDiscovery = null,
        IReadOnlyList<NativeSubmitRuntime>? nativeSubmitRuntimes = null,
        Func<TextSurfaceDiscoveryResult>? activeSurfaceDiscovery = null,
        Func<IntPtr, string?>? selectedWindowProfileResolver = null,
        Action<string>? protectedSendStageObserver = null,
        IDisposable? residentRuntimeOwner = null,
        IDisposable? nativeSubmitRuntimeOwner = null,
        Action? beforeProtectedSendTracePublishForTesting = null)
    {
        return new TrayProtectionController(
            hotkeyHost,
            applyOnlyRunner,
            nativeSubmitHookHost,
            nativeSubmitController,
            nativeProfile,
            enterprisePolicy,
            storageLayout,
            sendControlDiscovery,
            nativeSubmitRuntimes,
            activeSurfaceDiscovery,
            selectedWindowProfileResolver,
            protectedSendStageObserver,
            residentRuntimeOwner,
            nativeSubmitRuntimeOwner,
            beforeProtectedSendTracePublishForTesting);
    }

    // Explicit test seam for controller tests that do not construct the Windows orchestrator.
    internal static TrayProtectionController CreateTest(
        ITrayHotkeyHost hotkeyHost,
        Func<OsInteractionResult> applyOnlyRunner,
        INativeSubmitHookHost nativeSubmitHookHost,
        NativeSubmitInterceptionController nativeSubmitController,
        Func<OsInteractionResult> nativeSubmitRunner,
        SubmitBindingProfile? nativeProfile = null,
        NativeSubmitEnterprisePolicy? enterprisePolicy = null,
        DefaultStorageLayout? storageLayout = null,
        ISendControlDiscovery? sendControlDiscovery = null,
        Func<TextSurfaceDiscoveryResult>? activeSurfaceDiscovery = null,
        Func<IntPtr, string?>? selectedWindowProfileResolver = null,
        Action<string>? protectedSendStageObserver = null,
        IDisposable? residentRuntimeOwner = null,
        IDisposable? nativeSubmitRuntimeOwner = null,
        Action? beforeProtectedSendTracePublishForTesting = null)
    {
        ArgumentNullException.ThrowIfNull(nativeSubmitHookHost);
        ArgumentNullException.ThrowIfNull(nativeSubmitController);
        ArgumentNullException.ThrowIfNull(nativeSubmitRunner);

        var profile = nativeProfile ?? nativeSubmitController.Profile;
        var runtime = NativeSubmitRuntime.CreateTest(
            nativeSubmitHookHost,
            nativeSubmitController,
            nativeSubmitRunner,
            profile);
        return new TrayProtectionController(
            hotkeyHost,
            applyOnlyRunner,
            nativeSubmitHookHost,
            nativeSubmitController,
            profile,
            enterprisePolicy,
            storageLayout,
            sendControlDiscovery,
            nativeSubmitRuntimes: new[] { runtime },
            activeSurfaceDiscovery,
            selectedWindowProfileResolver,
            protectedSendStageObserver,
            residentRuntimeOwner,
            nativeSubmitRuntimeOwner,
            beforeProtectedSendTracePublishForTesting);
    }

    public event EventHandler? StateChanged;

    public TrayProtectionState State => ReadSnapshot().State;

    // Explicit test seam: observes only transitions whose snapshot CAS succeeded.
    internal void SetProtectedSendTracePublishedObserverForTesting(Action<ProtectedSendTraceEntry>? observer)
    {
        _protectedSendTracePublishedForTesting = observer;
    }

    internal bool IsNativeSubmitHookReady => ReadSnapshot().HookReady;

    internal void RefreshProjectFileProtectionStatus()
    {
        while (true)
        {
            var snapshot = ReadSnapshot();
            var (projectFilesProtected, projectFileStatus) = ReadProjectFileProtectionStatus();
            if (snapshot.State.ProjectFileStatus == projectFileStatus
                && snapshot.State.ProjectFilesProtected == projectFilesProtected)
            {
                return;
            }

            var replacement = snapshot with
            {
                State = snapshot.State with
                {
                    ProjectFileStatus = projectFileStatus,
                    ProjectFilesProtected = projectFilesProtected
                }
            };
            if (PublishSnapshotIfCurrent(snapshot, replacement))
            {
                return;
            }
        }
    }

    internal void PublishLocalProtectionStatus(string localProtectionStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localProtectionStatus);
        localProtectionStatus = LocalProtectionRecovery.ToSafeStatusCode(localProtectionStatus);
        if (string.Equals(localProtectionStatus, LocalProtectionRecovery.ReadyCode, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("local_protection_ready_requires_active_runtime");
        }

        while (true)
        {
            var snapshot = ReadSnapshot();
            var state = CarryRuntimeReloadSendState(
                snapshot.State with
                {
                    LastStatus = localProtectionStatus,
                    NativeSubmitEnabled = false,
                    NativeSubmitStatus = localProtectionStatus,
                    ProtectedSendBinding = "not_configured",
                    ReadinessStatus = localProtectionStatus,
                    ComposerProtected = false,
                    LocalProtectionStatus = localProtectionStatus
                },
                snapshot);
            var replacement = snapshot with
            {
                Generation = snapshot.Generation + 1,
                State = state
            };
            if (PublishSnapshotIfCurrent(snapshot, replacement))
            {
                CancelAndDrainActiveProtectedSendOperation(snapshot.RuntimeSet);
                return;
            }
        }
    }

    internal void PublishPromptProtectionRetryFailure()
    {
        while (true)
        {
            var snapshot = ReadSnapshot();
            if (snapshot.State.PromptProtectionRetryFailed)
            {
                return;
            }

            var replacement = snapshot with
            {
                State = snapshot.State with { PromptProtectionRetryFailed = true }
            };
            if (PublishSnapshotIfCurrent(snapshot, replacement))
            {
                return;
            }
        }
    }

    // Bounded deterministic seam for tray projection tests. Runtime ownership stays unchanged.
    internal void PublishSyntheticDiagnosticsForTesting(
        string localProtectionStatus,
        string projectFileStatus,
        string lastStatus,
        string lastProfileId,
        string protectedSendBinding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localProtectionStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFileStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedSendBinding);

        while (true)
        {
            var snapshot = ReadSnapshot();
            var replacement = snapshot with
            {
                State = snapshot.State with
                {
                    LocalProtectionStatus = localProtectionStatus,
                    ProjectFileStatus = projectFileStatus,
                    LastStatus = lastStatus,
                    LastProfileId = lastProfileId,
                    ProtectedSendBinding = protectedSendBinding
                }
            };
            if (PublishSnapshotIfCurrent(snapshot, replacement))
            {
                return;
            }
        }
    }

    internal bool TryPublishLocalProtectionReady()
    {
        while (true)
        {
            var snapshot = ReadSnapshot();
            if (!snapshot.State.Enabled
                || !snapshot.HookReady
                || snapshot.RuntimeSet is null
                || snapshot.RuntimeSet.Runtimes.Count == 0)
            {
                return false;
            }

            try
            {
                if (ReadSelectedProfileSetupReadiness(snapshot.RuntimeSet).Status
                    != OsInteractionStatusIds.Protected)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            var state = CarryRuntimeReloadSendState(
                snapshot.State with
                {
                    LastStatus = LocalProtectionRecovery.ReadyCode,
                    NativeSubmitEnabled = true,
                    NativeSubmitStatus = OsInteractionStatusIds.Protected,
                    ProtectedSendBinding = ProtectedSendBindingText(snapshot, OsInteractionStatusIds.Protected),
                    ReadinessStatus = OsInteractionStatusIds.Protected,
                    ComposerProtected = true,
                    SetupRequired = false,
                    LocalProtectionStatus = LocalProtectionRecovery.ReadyCode
                },
                snapshot);
            var replacement = snapshot with
            {
                Generation = snapshot.Generation + 1,
                State = state
            };
            if (PublishSnapshotIfCurrent(snapshot, replacement))
            {
                CancelAndDrainActiveProtectedSendOperation(snapshot.RuntimeSet);
                return true;
            }
        }
    }

    public bool Start()
    {
        var snapshot = ReadSnapshot();
        var setupReadiness = new SetupReadiness(
            SetupRequired: false,
            Status: OsInteractionStatusIds.Protected);
        if (snapshot.RuntimeSet is not null)
        {
            try
            {
                setupReadiness = ReadSelectedProfileSetupReadiness(snapshot.RuntimeSet);
            }
            catch
            {
                setupReadiness = new SetupReadiness(
                    SetupRequired: false,
                    Status: OsInteractionStatusIds.ProfilesUnavailable);
            }
        }
        var setupRequired = setupReadiness.SetupRequired;

        var manualHotkeyStarted = _hotkeyHost.Start(RunApplyOnlyOnce);
        var nativeStarted = snapshot.RuntimeSet is not null && StartNativeSubmitHook(snapshot.RuntimeSet);
        if (snapshot.RuntimeSet is not null && !nativeStarted)
        {
            var traceUnavailable = snapshot.RuntimeSet.Runtimes.Any(runtime => !HasRequiredResidentTraceRunner(runtime));
            StopAndDisposeRuntime(snapshot.RuntimeSet);
            if (manualHotkeyStarted)
            {
                _hotkeyHost.Stop();
            }

            var failedState = CreateState(
                enabled: false,
                lastStatus: traceUnavailable
                    ? OsInteractionStatusIds.TraceUnavailable
                    : NativeSubmitUnavailableStatus(snapshot),
                runtimes: snapshot.RuntimeSet.Runtimes,
                nativeSubmitStatus: traceUnavailable
                    ? OsInteractionStatusIds.TraceUnavailable
                    : OsInteractionStatusIds.NotConfigured,
                setupRequired: setupRequired,
                localProtectionStatus: snapshot.State.LocalProtectionStatus);
            PublishSnapshot(snapshot with
            {
                State = failedState with
                {
                    LastProtectedSendTraceStatus = traceUnavailable
                        ? "trace_unavailable"
                        : failedState.LastProtectedSendTraceStatus
                }
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
                    setupRequired: setupRequired,
                    localProtectionStatus: snapshot.State.LocalProtectionStatus)
            });
            return false;
        }

        var nativeStatus = nativeStarted
            ? setupReadiness.Status
            : NativeSubmitUnavailableStatus(snapshot);

        PublishSnapshot(snapshot with
        {
            State = CreateState(
                enabled: true,
                lastStatus: manualHotkeyStarted ? "enabled" : "enabled_native_submit_manual_hotkey_unavailable",
                runtimes: snapshot.RuntimeSet?.Runtimes ?? Array.Empty<NativeSubmitRuntime>(),
                nativeSubmitEnabled: nativeStarted && setupReadiness.Status == OsInteractionStatusIds.Protected,
                nativeSubmitStatus: nativeStatus,
                setupRequired: setupRequired,
                localProtectionStatus: snapshot.State.LocalProtectionStatus),
            HookReady = nativeStarted
        });
        return true;
    }

    public void Stop()
    {
        var operationToCancel = Volatile.Read(ref _activeProtectedSendOperation);
        if (operationToCancel is not null)
        {
            operationToCancel.RequestCancellation();
            ObserveProtectedSendStage("stop_cancellation_requested");
        }

        lock (_reloadGate)
        {
            ProtectionSnapshot snapshot;
            while (true)
            {
                snapshot = ReadSnapshot();
                var disabledState = CreateState(
                    enabled: false,
                    lastStatus: "disabled",
                    runtimes: Array.Empty<NativeSubmitRuntime>(),
                    localProtectionStatus: snapshot.State.LocalProtectionStatus);
                // Keep the operation captured before the reload lock even if
                // its callback completes while Stop is waiting to publish the
                // disabled generation.
                var activeOperation = Volatile.Read(ref _activeProtectedSendOperation) ?? operationToCancel;
                if (ActiveAttemptInterruptedByRuntimeReload(snapshot.State))
                {
                    disabledState = CarryInterruptedAttemptState(
                        disabledState,
                        snapshot.State,
                        snapshot.Generation,
                        "protection_stopped",
                        activeOperation);
                }
                else
                {
                    if (activeOperation is not null
                        && ReferenceEquals(activeOperation.RuntimeSet, snapshot.RuntimeSet))
                    {
                        disabledState = CarryUnpublishedAttemptState(
                            disabledState,
                            activeOperation,
                            snapshot.Generation,
                            "protection_stopped");
                    }
                }
                var disabledSnapshot = snapshot with
                {
                    State = disabledState,
                    RuntimeSet = null,
                    HookReady = false
                };
                if (PublishSnapshotIfCurrent(snapshot, disabledSnapshot))
                {
                    break;
                }
            }

            // Publish the disabled generation before cancelling or stopping the
            // old hook. A queued callback can then only observe the fail-closed
            // snapshot and cannot enroll a new protected operation.
            CancelAndDrainActiveProtectedSendOperation(snapshot.RuntimeSet);
            StopAndDisposeRuntime(snapshot.RuntimeSet);
            var residentRuntimeOwner = _residentRuntimeOwner;
            _residentRuntimeOwner = null;
            residentRuntimeOwner?.Dispose();
            _hotkeyHost.Stop();
        }
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
            var reloaded = ReloadRuntime(
                previous,
                runtime.NativeSubmitRuntimeSet,
                runtime.ApplyOnlyRunner,
                runtime.ApplyOnlyResourceOwner);
            if (!reloaded)
            {
                runtime.ApplyOnlyResourceOwner?.Dispose();
            }

            return reloaded;
        }
    }

    private bool ReloadRuntime(
        ProtectionSnapshot previous,
        NativeSubmitRuntimeSet runtimeSet,
        Func<OsInteractionResult> applyOnlyRunner,
        IDisposable? residentRuntimeOwner = null)
    {
        var activeOperation = Volatile.Read(ref _activeProtectedSendOperation);
        if (activeOperation is not null
            && ReferenceEquals(activeOperation.RuntimeSet, previous.RuntimeSet))
        {
            activeOperation.RequestCancellation();
            ObserveProtectedSendStage("reload_cancellation_requested");
        }

        var candidate = TryBuildCandidateSnapshot(
            previous,
            runtimeSet,
            applyOnlyRunner,
            activeOperation);
        if (candidate is null)
        {
            return false;
        }

        if (!previous.State.Enabled)
        {
            // A candidate must never become the active protected runtime while
            // the resident gate is disabled or its hook is not ready.
            return false;
        }

        if (!StartNativeSubmitHook(candidate.RuntimeSet!))
        {
            StopAndDisposeRuntime(candidate.RuntimeSet);
            residentRuntimeOwner?.Dispose();
            return false;
        }

        if (!TryPublishRuntimeCandidate(previous, candidate, activeOperation))
        {
            StopAndDisposeRuntime(candidate.RuntimeSet);
            residentRuntimeOwner?.Dispose();
            return false;
        }

        if (residentRuntimeOwner is not null
            && !ReferenceEquals(_residentRuntimeOwner, residentRuntimeOwner))
        {
            var previousOwner = _residentRuntimeOwner;
            _residentRuntimeOwner = residentRuntimeOwner;
            previousOwner?.Dispose();
        }

        if (previous.RuntimeSet is not null
            && !ReferenceEquals(previous.RuntimeSet.HookHost, candidate.RuntimeSet!.HookHost))
        {
            CancelAndDrainActiveProtectedSendOperation(previous.RuntimeSet);
            StopAndDisposeRuntime(previous.RuntimeSet);
        }

        return true;
    }

    private bool TryPublishRuntimeCandidate(
        ProtectionSnapshot previous,
        ProtectionSnapshot candidate,
        ResidentProtectedSendOperation? interruptedOperation = null)
    {
        while (true)
        {
            var current = ReadSnapshot();
            if (current.Generation != previous.Generation)
            {
                return false;
            }

            var state = ReferenceEquals(current, previous)
                ? CarryRuntimeReloadSendState(candidate.State, previous, interruptedOperation)
                : CarryRuntimeReloadSendState(candidate.State, current, interruptedOperation) with
                {
                    Enabled = current.State.Enabled,
                    Mode = current.State.Mode,
                    Hotkey = current.State.Hotkey,
                    LastStatus = current.State.LastStatus,
                    LastDecision = current.State.LastDecision,
                    LastReplacementCount = current.State.LastReplacementCount,
                    LastProfileId = current.State.LastProfileId,
                    LastApplied = current.State.LastApplied,
                    LastSubmitted = current.State.LastSubmitted,
                    ProjectFilesProtected = current.State.ProjectFilesProtected,
                    ProjectFileStatus = current.State.ProjectFileStatus,
                    ResidentProcess = current.State.ResidentProcess,
                    ProgrammaticUiaInvokeStatus = current.State.ProgrammaticUiaInvokeStatus,
                    LocalProtectionStatus = current.State.LocalProtectionStatus,
                    PromptProtectionRetryFailed = current.State.PromptProtectionRetryFailed,
                    SetupVerificationStatus = current.State.SetupVerificationStatus,
                    SetupVerificationAction = current.State.SetupVerificationAction,
                    SetupVerificationProfileId = current.State.SetupVerificationProfileId,
                    SetupVerificationBinding = current.State.SetupVerificationBinding,
                    SetupVerificationAttemptId = current.State.SetupVerificationAttemptId
                };
            if (!string.Equals(
                    state.LocalProtectionStatus,
                    LocalProtectionRecovery.ReadyCode,
                    StringComparison.Ordinal))
            {
                state = state with
                {
                    LastStatus = state.LocalProtectionStatus,
                    NativeSubmitEnabled = false,
                    NativeSubmitStatus = state.LocalProtectionStatus,
                    ProtectedSendBinding = "not_configured",
                    ReadinessStatus = state.LocalProtectionStatus,
                    ComposerProtected = false
                };
            }

            var replacement = candidate with
            {
                Generation = ReferenceEquals(current, previous)
                    ? candidate.Generation
                    : current.Generation + 1,
                State = state
            };
            if (PublishSnapshotIfCurrent(current, replacement))
            {
                CancelAndDrainActiveProtectedSendOperation(current.RuntimeSet);
                return true;
            }
        }
    }

    /// <summary>
    /// Gets the current protection snapshot for reading
    /// This is an atomic, immutable view of all protection state
    /// </summary>
    public ProtectionSnapshot GetCurrentSnapshot()
    {
        return ReadSnapshot();
    }

    internal void PublishSetupVerificationProgress(PromptProtectionSetupProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        while (true)
        {
            var current = ReadSnapshot();
            var status = PromptProtectionSetupLifecycle.SafeStatus(progress.Status);
            var attemptId = progress.AttemptId;
            if (status == "waiting_for_focus")
            {
                if (attemptId <= current.State.SetupVerificationAttemptId
                    || current.State.SetupVerificationStatus is "waiting_for_focus" or "composer_recognized"
                        or "verifying_binding" or "activating_protection")
                {
                    return;
                }
            }
            else if (attemptId == 0
                || attemptId != current.State.SetupVerificationAttemptId
                || !PromptProtectionSetupLifecycle.IsAllowedTransition(current.State.SetupVerificationStatus, status))
            {
                return;
            }

            var replacement = current with
            {
                State = current.State with
                {
                    SetupVerificationStatus = status,
                    SetupVerificationAction = PromptProtectionSetupLifecycle.SafeAction(progress.Action),
                    SetupVerificationProfileId = PromptProtectionSetupLifecycle.SafeProfileId(progress.ProfileId),
                    SetupVerificationBinding = PromptProtectionSetupLifecycle.SafeBinding(progress.Binding),
                    SetupVerificationAttemptId = attemptId
                }
            };
            if (PublishSnapshotIfCurrent(current, replacement))
            {
                return;
            }
        }
    }

    internal bool IsCurrentSetupVerificationAttempt(long attemptId)
    {
        return attemptId > 0 && ReadSnapshot().State.SetupVerificationAttemptId == attemptId;
    }

    /// <summary>
    /// Builds a candidate snapshot from runtime set, validating all components
    /// Returns null if validation fails (retaining previous complete snapshot)
    /// </summary>
    private ProtectionSnapshot? TryBuildCandidateSnapshot(
        ProtectionSnapshot previous,
        NativeSubmitRuntimeSet runtimeSet,
        Func<OsInteractionResult> applyOnlyRunner,
        ResidentProtectedSendOperation? interruptedOperation = null)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(applyOnlyRunner);
            var runtimes = runtimeSet.Runtimes?.ToArray() ?? Array.Empty<NativeSubmitRuntime>();
            if (runtimeSet.HookHost is null
                || runtimes.Length == 0
                || runtimes.Any(runtime => runtime is null
                    || runtime.Controller is null
                    || runtime.Profile is null
                    || string.IsNullOrWhiteSpace(runtime.Profile.ProfileId)
                    || !HasRequiredResidentTraceRunner(runtime)))
            {
                return null;
            }

            var candidateRuntimeSet = new NativeSubmitRuntimeSet(
                runtimeSet.HookHost,
                runtimes,
                runtimeSet.ResourceOwner);
            var setupReadiness = ReadSelectedProfileSetupReadiness(candidateRuntimeSet);
            var setupRequired = setupReadiness.SetupRequired;
            var state = CreateState(
                enabled: previous.State.Enabled,
                lastStatus: "native_submit_runtime_reloaded",
                runtimes: candidateRuntimeSet.Runtimes,
                nativeSubmitEnabled: previous.State.Enabled
                    && setupReadiness.Status == OsInteractionStatusIds.Protected,
                nativeSubmitStatus: previous.State.Enabled
                    ? setupReadiness.Status
                    : OsInteractionStatusIds.NotConfigured,
                setupRequired: setupRequired,
                localProtectionStatus: previous.State.LocalProtectionStatus);
            state = CarryRuntimeReloadSendState(state, previous, interruptedOperation) with
            {
                SetupVerificationStatus = previous.State.SetupVerificationStatus,
                SetupVerificationAction = previous.State.SetupVerificationAction,
                SetupVerificationProfileId = previous.State.SetupVerificationProfileId,
                SetupVerificationBinding = previous.State.SetupVerificationBinding,
                SetupVerificationAttemptId = previous.State.SetupVerificationAttemptId
            };

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
            SetupRequired: snapshot.State.SetupRequired,
            ProgrammaticUiaInvokeStatus: snapshot.State.ProgrammaticUiaInvokeStatus,
            LocalProtectionStatus: snapshot.State.LocalProtectionStatus,
            PromptProtectionRetryFailed: snapshot.State.PromptProtectionRetryFailed,
            ConfiguredProfileId: snapshot.State.ConfiguredProfileId,
            ProtectedSendAttemptStatus: snapshot.State.ProtectedSendAttemptStatus,
            ProtectedSendAttemptAction: snapshot.State.ProtectedSendAttemptAction,
            ProtectedSendAttemptId: snapshot.State.ProtectedSendAttemptId,
            ProtectedSendAttemptTrace: snapshot.State.ProtectedSendAttemptTrace,
            ProtectedSendAttemptStartedAtTimestamp: snapshot.State.ProtectedSendAttemptStartedAtTimestamp,
            LastProtectedSendInterruption: snapshot.State.LastProtectedSendInterruption,
            SetupVerificationStatus: snapshot.State.SetupVerificationStatus,
            SetupVerificationAction: snapshot.State.SetupVerificationAction,
            SetupVerificationProfileId: snapshot.State.SetupVerificationProfileId,
            SetupVerificationBinding: snapshot.State.SetupVerificationBinding,
            SetupVerificationAttemptId: snapshot.State.SetupVerificationAttemptId,
            ProtectedClaimStatus: snapshot.State.ProtectedClaimStatus,
            ReferenceAcceptanceStatus: snapshot.State.ReferenceAcceptanceStatus,
            LiveContractStatus: snapshot.State.LiveContractStatus);
        PublishSnapshotIfCurrent(snapshot, snapshot with { State = state });
    }

    private bool StartNativeSubmitHook(NativeSubmitRuntimeSet runtimeSet)
    {
        if (runtimeSet.Runtimes.Any(runtime => !HasRequiredResidentTraceRunner(runtime)))
        {
            return false;
        }

        var keyboardStarted = runtimeSet.HookHost.Start(
            gesture => ClassifyNativeGesture(runtimeSet, gesture),
            (gesture, classification) => RunNativeSubmitOnce(runtimeSet, gesture, classification),
            gesture => ShouldSuppressKeyboardClassificationFailure(runtimeSet, gesture));
        if (!keyboardStarted)
        {
            return false;
        }

        if (runtimeSet.HookHost is INativeSubmitPointerHookHost pointerHook
            && ReadSnapshot().SendControlDiscovery is not null)
        {
            if (!pointerHook.StartPointer(
                gesture => ClassifySendControl(runtimeSet, gesture),
                (gesture, classification) => RunNativeSendControlOnce(runtimeSet, gesture, classification),
                gesture => ShouldSuppressPointerClassificationFailure(runtimeSet, gesture)))
            {
                StopAndDisposeRuntime(runtimeSet);
                return false;
            }
        }

        return true;
    }

    private NativeSubmitInterceptionResult ClassifySendControl(
        NativeSubmitRuntimeSet runtimeSet,
        NativePointerGesture gesture)
    {
        var snapshot = ReadSnapshot();
        if (snapshot.SendControlDiscovery is null)
        {
            return RememberSnapshot(snapshot, runtimeSet, PassThroughPointer(), target: null);
        }

        var discovery = snapshot.SendControlDiscovery.Discover(gesture);
        RememberCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId, discovery.ComposerDiscovery);
        var runtime = ResolveRuntime(snapshot, runtimeSet, discovery.ComposerDiscovery)
            ?? ResolveRuntimeByProfileIdentity(snapshot, runtimeSet, discovery.ComposerDiscovery);
        var target = NativeSubmitTargetIdentity.TryCreateForGesture(
            snapshot.Generation,
            discovery.ComposerDiscovery.Surface,
            gesture.TargetWindow);
        var result = discovery.Classification switch
        {
            SendControlClassification.IdentifiedSend when runtime is not null
                => target is null
                    ? TraceUnavailablePointerSubmit(runtime.Profile.ProfileId)
                    : IsLocalProtectionReady(snapshot)
                    ? runtime.Controller.HandleIdentifiedSendControl(discovery.ComposerDiscovery)
                    : SuppressLocalProtectionRecoverySubmit(runtime.Profile.ProfileId),
            SendControlClassification.SelectedClientUncertain
                => SuppressUncertainSelectedSend(SelectedClientProfileId(discovery.ComposerDiscovery, runtime)),
            _ => PassThroughPointer()
        };
        return RememberSnapshot(snapshot, runtimeSet, result, target);
    }

    private bool ShouldSuppressPointerClassificationFailure(
        NativeSubmitRuntimeSet runtimeSet,
        NativePointerGesture gesture)
    {
        if (gesture.TargetWindow == IntPtr.Zero)
        {
            return false;
        }

        var profileId = LookupCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId);

        return !string.IsNullOrWhiteSpace(profileId)
            && runtimeSet.Runtimes.Any(runtime => string.Equals(
                runtime.Profile.ProfileId,
                profileId,
                StringComparison.Ordinal)) == true;
    }

    private bool ShouldSuppressKeyboardClassificationFailure(
        NativeSubmitRuntimeSet runtimeSet,
        NativeKeyGesture gesture)
    {
        if (gesture.TargetWindow == IntPtr.Zero)
        {
            return false;
        }

        var profileId = LookupCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId);

        var runtime = string.IsNullOrWhiteSpace(profileId)
            ? null
            : runtimeSet.Runtimes.FirstOrDefault(candidate => string.Equals(
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

    private void RunNativeSendControlOnce(
        NativeSubmitRuntimeSet runtimeSet,
        NativePointerGesture gesture,
        NativeSubmitInterceptionResult classification)
    {
        if (!TryTakeExecutionContext(classification, out var execution))
        {
            PublishUnattributedClassificationFailure(runtimeSet, classification);
            return;
        }

        var snapshot = execution.Snapshot;
        var currentSnapshot = ReadSnapshot();
        if (!ReferenceEquals(currentSnapshot, snapshot)
            || !ReferenceEquals(currentSnapshot.RuntimeSet, runtimeSet)
            || !IsLocalProtectionReady(snapshot))
        {
            PublishStaleCapturedAttempt(
                snapshot,
                runtimeSet,
                execution.Target,
                classification.Diagnostics.TryGetValue("profile_id", out var staleProfileId)
                    ? staleProfileId
                    : execution.Target?.ProfileId ?? snapshot.State.ConfiguredProfileId ?? "selected_client",
                currentSnapshot.State.Enabled ? "runtime_replaced" : "protection_stopped");
            return;
        }

        var runtime = ResolveClassifiedRuntime(runtimeSet, classification);
        if (classification.Status == OsInteractionStatusIds.TraceUnavailable
            && classification.Diagnostics.ContainsKey("pointer_target_identity"))
        {
            if (runtime is null)
            {
                PublishBlockedNativeSubmitState(snapshot, classification, profileId: null);
                return;
            }

            if (!TryRunProtectedSendOperation(
                    snapshot,
                    runtimeSet,
                    execution.Target,
                    operation => RunTraceUnavailableProtectedSendFlow(
                        snapshot,
                        runtime,
                        classification,
                        operation),
                    out var traceUnavailableResult))
            {
                PublishTraceUnavailable(snapshot, runtime.Profile.ProfileId);
                return;
            }

            PublishNativeSubmitState(
                ReadSnapshot(),
                traceUnavailableResult.Status,
                OsInteractionStatusIds.TraceUnavailable,
                runtime.Profile.ProfileId,
                applied: false,
                submitted: false,
                diagnostics: traceUnavailableResult.Diagnostics);
            return;
        }

        if (classification.Status != OsInteractionStatusIds.NativeSubmitGuarded)
        {
            PublishBlockedNativeSubmitState(snapshot, classification, runtime?.Profile.ProfileId);
            return;
        }

        if (runtime is null)
        {
            PublishBlockedNativeSubmitState(snapshot, classification, profileId: null);
            return;
        }

        if (!TryRunProtectedSendOperation(
                snapshot,
                runtimeSet,
                execution.Target,
                operation => RunProtectedNativeSubmitFlow(snapshot, runtime, classification, operation),
                out var result))
        {
            if (Volatile.Read(ref _activeProtectedSendOperation) is not null)
            {
                var current = ReadSnapshot();
                PublishNativeSubmitState(current,
                    OsInteractionStatusIds.NativeSubmitInProgress,
                    OsInteractionStatusIds.Protected,
                    runtime.Profile.ProfileId,
                    applied: false,
                    submitted: false);
            }

            return;
        }

        PublishNativeSubmitState(ReadSnapshot(),
            result.Status,
            NativeSubmitReadinessStatusAfterFlow(result.Status),
            runtime.Profile.ProfileId,
            result.Applied,
            result.Submitted,
            diagnostics: result.Diagnostics);
    }

    private NativeSubmitInterceptionResult RunTraceUnavailableProtectedSendFlow(
        ProtectionSnapshot eventSnapshot,
        NativeSubmitRuntime runtime,
        NativeSubmitInterceptionResult classification,
        ResidentProtectedSendOperation operation)
    {
        var detectedTraceSnapshot = PublishProtectedSendTrace(
            eventSnapshot,
            operation,
            "send_detected",
            "checking_prompt",
            "detected",
            "checking_prompt");
        if (detectedTraceSnapshot is null)
        {
            return FailedClosedNativeSubmitResult();
        }

        if (PublishProtectedSendTrace(
                detectedTraceSnapshot,
                operation,
                "terminal_blocked",
                OsInteractionStatusIds.TraceUnavailable,
                "trace_unavailable",
                "retry_protection") is null)
        {
            return FailedClosedNativeSubmitResult();
        }

        return classification;
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

    private static NativeSubmitInterceptionResult TraceUnavailablePointerSubmit(string profileId)
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.TraceUnavailable,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["profile_id"] = profileId,
                ["trace_status"] = "trace_unavailable",
                ["pointer_target_identity"] = "unavailable"
            });
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

    private static string SelectedClientProfileId(
        TextSurfaceDiscoveryResult discovery,
        NativeSubmitRuntime? runtime)
    {
        return runtime?.Profile.ProfileId
            ?? discovery.Surface?.ProfileId
            ?? (discovery.Diagnostics.TryGetValue("profile_id", out var profileId) ? profileId : null)
            ?? "selected_client";
    }

    private static NativeSubmitInterceptionResult SuppressLocalProtectionRecoverySubmit(string profileId)
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.NativeSubmitGuarded,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["profile_id"] = profileId,
                ["local_protection"] = "unavailable"
            });
    }

    private void RunNativeSubmitOnce(
        NativeSubmitRuntimeSet runtimeSet,
        NativeKeyGesture gesture,
        NativeSubmitInterceptionResult classification)
    {
        if (!TryTakeExecutionContext(classification, out var execution))
        {
            PublishUnattributedClassificationFailure(runtimeSet, classification);
            return;
        }

        var snapshot = execution.Snapshot;
        var currentSnapshot = ReadSnapshot();
        if (!ReferenceEquals(currentSnapshot, snapshot)
            || !ReferenceEquals(currentSnapshot.RuntimeSet, runtimeSet)
            || !IsLocalProtectionReady(snapshot))
        {
            PublishStaleCapturedAttempt(
                snapshot,
                runtimeSet,
                execution.Target,
                classification.Diagnostics.TryGetValue("profile_id", out var staleProfileId)
                    ? staleProfileId
                    : execution.Target?.ProfileId ?? snapshot.State.ConfiguredProfileId ?? "selected_client",
                currentSnapshot.State.Enabled ? "runtime_replaced" : "protection_stopped");
            return;
        }

        var runtime = ResolveClassifiedRuntime(runtimeSet, classification);
        if (classification.Status != OsInteractionStatusIds.NativeSubmitGuarded)
        {
            PublishBlockedNativeSubmitState(snapshot, classification, runtime?.Profile.ProfileId);
            return;
        }

        if (runtime is null)
        {
            PublishBlockedNativeSubmitState(snapshot, classification, profileId: null);
            return;
        }

        if (!TryRunProtectedSendOperation(
                snapshot,
                runtimeSet,
                execution.Target,
                operation => RunProtectedNativeSubmitFlow(snapshot, runtime, classification, operation),
                out var protectedResult))
        {
            if (Volatile.Read(ref _activeProtectedSendOperation) is not null)
            {
                var current = ReadSnapshot();
                PublishNativeSubmitState(current,
                    OsInteractionStatusIds.NativeSubmitInProgress,
                    readinessStatus: OsInteractionStatusIds.Protected,
                    profileId: runtime.Profile.ProfileId,
                    applied: false,
                    submitted: false);
            }

            return;
        }

        var publishedStatus = NativeSubmitFlowStatusForPublication(protectedResult);
        var readinessStatus = NativeSubmitReadinessStatusAfterFlow(publishedStatus);
        var setupRequired = readinessStatus == OsInteractionStatusIds.NativeSubmitSetupRequired;
        PublishNativeSubmitState(ReadSnapshot(),
            publishedStatus,
            readinessStatus,
            protectedResult.Diagnostics.TryGetValue("profile_id", out var profileId) ? profileId : runtime.Profile.ProfileId,
            protectedResult.Applied,
            protectedResult.Submitted,
            setupRequired,
            diagnostics: protectedResult.Diagnostics);
    }

    private NativeSubmitInterceptionResult RunProtectedNativeSubmitFlow(
        ProtectionSnapshot eventSnapshot,
        NativeSubmitRuntime runtime,
        NativeSubmitInterceptionResult classification,
        ResidentProtectedSendOperation operation)
    {
        var snapshot = eventSnapshot;
        var detectedTraceSnapshot = PublishProtectedSendTrace(
            snapshot,
            operation,
            "send_detected",
            "checking_prompt",
            "detected",
            "checking_prompt");
        if (detectedTraceSnapshot is null)
        {
            PublishTraceUnavailable(snapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        var checkingSnapshot = PublishProtectedSendAttempt(
            detectedTraceSnapshot,
            operation,
            "checking",
            "checking_prompt");
        if (checkingSnapshot is null)
        {
            PublishTraceUnavailable(snapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        snapshot = checkingSnapshot;
        ObserveProtectedSendStage("detection");
        ObserveProtectedSendStage("checking");

        var targetMatchedSnapshot = PublishProtectedSendTrace(
            snapshot,
            operation,
            "target_matched",
            "target_verified");
        if (targetMatchedSnapshot is null)
        {
            PublishTraceUnavailable(snapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        snapshot = targetMatchedSnapshot;

        bool TraceStage(string stage, string resultCode)
        {
            ObserveProtectedSendStage(stage switch
            {
                "overlay_created" or "overlay_foreground_confirmed" => "overlay",
                "text_written" => "write",
                "send_injected" => "replay",
                _ => stage
            });
            var tracedSnapshot = PublishProtectedSendTrace(snapshot, operation, stage, resultCode);
            if (tracedSnapshot is null)
            {
                return false;
            }

            snapshot = tracedSnapshot;
            return true;
        }

        var result = runtime.Controller.CompleteGuardedSubmit(
            classification,
            () => RunNativeSubmitFlow(
                runtime,
                operation.Target,
                TraceStage,
                () => CanContinueProtectedSendOperation(operation),
                () => AcquireProtectedSendSideEffect(operation)));

        var terminalTrace = result.Submitted
            ? PublishProtectedSendTrace(
                snapshot,
                operation,
                "sent_safely",
                result.Status,
                ProtectedSendAttemptStatus(result.Status, submitted: true),
                ProtectedSendAttemptAction(result.Status, submitted: true))
            : PublishProtectedSendTrace(
                snapshot,
                operation,
                "terminal_blocked",
                result.Status,
                ProtectedSendAttemptStatus(result.Status, submitted: false),
                ProtectedSendAttemptAction(result.Status, submitted: false));
        if (terminalTrace is null)
        {
            PublishTraceUnavailable(snapshot, runtime.Profile.ProfileId);
            return FailedClosedNativeSubmitResult();
        }

        return result;
    }

    private void PublishNativeSubmitState(
        ProtectionSnapshot eventSnapshot,
        string lastStatus,
        string readinessStatus,
        string? profileId,
        bool applied,
        bool submitted,
        bool setupRequired = false,
        IReadOnlyDictionary<string, string>? diagnostics = null)
    {
        while (true)
        {
            var current = ReadSnapshot();
            if (!CanContinueWithRuntime(current, eventSnapshot)
                || (lastStatus != OsInteractionStatusIds.NativeSubmitInProgress
                    && current.State.ProtectedSendAttemptId != eventSnapshot.State.ProtectedSendAttemptId))
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
                NativeSubmitEnabled: readinessStatus == OsInteractionStatusIds.Protected,
                NativeSubmitStatus: readinessStatus,
                ProtectedSendBinding: ProtectedSendBindingText(current, readinessStatus, profileId),
                NewlineBinding: NewlineBindingText(current),
                ManualScanHotkey: _hotkeyHost.Binding.DisplayText,
                ReadinessStatus: readinessStatus,
                ComposerProtected: readinessStatus == OsInteractionStatusIds.Protected,
                ProjectFilesProtected: current.State.ProjectFilesProtected,
                ProjectFileStatus: current.State.ProjectFileStatus,
                ResidentProcess: true,
                SetupRequired: setupRequired,
                ProgrammaticUiaInvokeStatus: current.State.ProgrammaticUiaInvokeStatus,
                LocalProtectionStatus: current.State.LocalProtectionStatus,
                PromptProtectionRetryFailed: current.State.PromptProtectionRetryFailed,
                ConfiguredProfileId: current.State.ConfiguredProfileId,
                ProtectedSendAttemptStatus: ProtectedSendAttemptStatus(lastStatus, submitted),
                ProtectedSendAttemptAction: ProtectedSendAttemptAction(lastStatus, submitted),
                ProtectedSendAttemptId: current.State.ProtectedSendAttemptId,
                ProtectedSendAttemptTrace: current.State.ProtectedSendAttemptTrace,
                ProtectedSendAttemptStartedAtTimestamp: current.State.ProtectedSendAttemptStartedAtTimestamp,
                LastProtectedSendInterruption: current.State.LastProtectedSendInterruption,
                LastProtectedSendTraceStatus: ProtectedSendTraceStatus(diagnostics, current.State.LastProtectedSendTraceStatus),
                SetupVerificationStatus: current.State.SetupVerificationStatus,
                SetupVerificationAction: current.State.SetupVerificationAction,
                SetupVerificationProfileId: current.State.SetupVerificationProfileId,
                SetupVerificationBinding: current.State.SetupVerificationBinding,
                SetupVerificationAttemptId: current.State.SetupVerificationAttemptId,
                ProtectedClaimStatus: current.State.ProtectedClaimStatus,
                ReferenceAcceptanceStatus: current.State.ReferenceAcceptanceStatus,
                LiveContractStatus: current.State.LiveContractStatus);
            if (PublishSnapshotIfCurrent(current, current with { State = state }))
            {
                return;
            }
        }
    }

    private static string ProtectedSendTraceStatus(
        IReadOnlyDictionary<string, string>? diagnostics,
        string currentStatus)
    {
        if (diagnostics is null
            || !diagnostics.TryGetValue("trace_status", out var traceStatus))
        {
            return "none";
        }

        return traceStatus switch
        {
            "trace_unavailable"
                or "test_trace_unavailable"
                or "resident_operation_unavailable"
                or "send_injected_unavailable" => traceStatus,
            _ => "none"
        };
    }

    private ProtectionSnapshot? PublishProtectedSendAttempt(
        ProtectionSnapshot snapshot,
        ResidentProtectedSendOperation operation,
        string status,
        string action)
    {
        while (true)
        {
            var current = ReadSnapshot();
            if (!CanContinueWithRuntime(current, snapshot))
            {
                return null;
            }

            var replacement = current with
            {
                State = current.State with
                {
                    ProtectedSendAttemptStatus = status,
                    ProtectedSendAttemptAction = action,
                    ProtectedSendAttemptId = operation.AttemptId,
                    ProtectedSendAttemptTrace = operation.Trace,
                    ProtectedSendAttemptStartedAtTimestamp = operation.StartedAtTimestamp,
                    LastProtectedSendInterruption = null
                }
            };
            if (PublishSnapshotIfCurrent(current, replacement))
            {
                return replacement;
            }
        }
    }

    private ProtectionSnapshot? PublishProtectedSendTrace(
        ProtectionSnapshot snapshot,
        ResidentProtectedSendOperation operation,
        string stage,
        string resultCode,
        string? attemptStatus = null,
        string? attemptAction = null)
    {
        return ProtectedSendTraceTransition.TryCreate(stage, resultCode, out var transition)
            ? PublishProtectedSendTrace(snapshot, operation, transition, attemptStatus, attemptAction)
            : null;
    }

    private ProtectionSnapshot? PublishProtectedSendTrace(
        ProtectionSnapshot snapshot,
        ResidentProtectedSendOperation operation,
        ProtectedSendTraceTransition transition,
        string? attemptStatus = null,
        string? attemptAction = null)
    {
        return PublishOperationTraceTransaction(
            snapshot,
            operation,
            attemptStatus,
            attemptAction,
            allowCancelledOperation: false,
            tryPublish => operation.TryAppendTraceTransaction(
                transition.StageToken,
                transition.ResultCode.Value,
                DurationSince(operation.StartedAtTimestamp),
                tryPublish,
                out _));
    }

    private ProtectionSnapshot? PublishTerminalBlockedTrace(
        ProtectionSnapshot snapshot,
        ResidentProtectedSendOperation operation)
    {
        return PublishOperationTraceTransaction(
            snapshot,
            operation,
            "trace_unavailable",
            "retry_protection",
            allowCancelledOperation: true,
            tryPublish => operation.TryEnsureTerminalBlockedTraceTransaction(tryPublish, out _));
    }

    private ProtectionSnapshot? PublishOperationTraceTransaction(
        ProtectionSnapshot snapshot,
        ResidentProtectedSendOperation operation,
        string? attemptStatus,
        string? attemptAction,
        bool allowCancelledOperation,
        Func<Func<IReadOnlyList<ProtectedSendTraceEntry>, bool>, bool> commit)
    {
        ProtectionSnapshot? published = null;
        if (!commit(trace => TryPublishOperationTraceSnapshot(
                snapshot,
                operation,
                trace,
                attemptStatus,
                attemptAction,
                allowCancelledOperation,
                out published)))
        {
            return null;
        }

        NotifyStateChanged();
        return published;
    }

    private bool TryPublishOperationTraceSnapshot(
        ProtectionSnapshot source,
        ResidentProtectedSendOperation operation,
        IReadOnlyList<ProtectedSendTraceEntry> trace,
        string? attemptStatus,
        string? attemptAction,
        bool allowCancelledOperation,
        out ProtectionSnapshot? published)
    {
        published = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (operation.IsCancelled && !allowCancelledOperation)
            {
                return false;
            }

            var current = ReadSnapshot();
            if (!CanContinueWithRuntime(current, source))
            {
                return false;
            }

            var state = current.State with
            {
                ProtectedSendAttemptId = operation.AttemptId,
                ProtectedSendAttemptTrace = trace,
                ProtectedSendAttemptStartedAtTimestamp = operation.StartedAtTimestamp,
                ProtectedSendAttemptStatus = attemptStatus ?? current.State.ProtectedSendAttemptStatus,
                ProtectedSendAttemptAction = attemptAction ?? current.State.ProtectedSendAttemptAction,
                LastProtectedSendInterruption = null
            };
            if (attemptStatus == "trace_unavailable")
            {
                var enabled = current.State.Enabled;
                state = state with
                {
                    LastProtectedSendTraceStatus = "trace_unavailable",
                    LastStatus = enabled ? OsInteractionStatusIds.TraceUnavailable : current.State.LastStatus,
                    LastApplied = false,
                    LastSubmitted = false,
                    NativeSubmitEnabled = enabled ? false : current.State.NativeSubmitEnabled,
                    NativeSubmitStatus = enabled
                        ? OsInteractionStatusIds.TraceUnavailable
                        : current.State.NativeSubmitStatus,
                    ReadinessStatus = enabled
                        ? OsInteractionStatusIds.TraceUnavailable
                        : current.State.ReadinessStatus,
                    ComposerProtected = enabled ? false : current.State.ComposerProtected,
                    ProtectedSendBinding = ProtectedSendBindingText(
                        current,
                        enabled ? OsInteractionStatusIds.TraceUnavailable : current.State.ReadinessStatus,
                        operation.Target?.ProfileId ?? current.State.LastProfileId)
                };
            }

            var replacement = current with { State = state };
            _beforeProtectedSendTracePublishForTesting?.Invoke();
            if (operation.TryPublishIfCancellationAllows(
                    allowCancelledOperation,
                    () => TryReplaceSnapshotIfCurrent(current, replacement)))
            {
                try
                {
                    _protectedSendTracePublishedForTesting?.Invoke(trace[^1]);
                }
                catch
                {
                    // A test observer must not change production publication semantics.
                }

                published = replacement;
                if (trace[^1].Stage == "sent_safely")
                {
                    var liveProfileId = operation.Target?.ProfileId
                        ?? replacement.State.LastProfileId
                        ?? replacement.State.ConfiguredProfileId;
                    var liveProfile = replacement.RuntimeSet?.Runtimes.FirstOrDefault(runtime =>
                        string.Equals(runtime.Profile.ProfileId, liveProfileId, StringComparison.Ordinal))?.Profile;
                    if (liveProfile is not null
                        && ChatGptAcceptanceProofStore.IsLiveContractArmed(
                            _storageLayout,
                            liveProfile,
                            BuildVersion.Current)
                        && ChatGptAcceptanceProofStore.RecordLiveContract(
                            _storageLayout,
                            liveProfile,
                            BuildVersion.Current,
                            trace))
                    {
                        PublishChatGptProtectedClaim(liveProfile.ProfileId);
                    }
                }

                return true;
            }

            if (operation.IsCancelled && !allowCancelledOperation)
            {
                return false;
            }
        }

        return false;
    }

    private void PublishChatGptProtectedClaim(string profileId)
    {
        while (true)
        {
            var current = ReadSnapshot();
            var profile = current.RuntimeSet?.Runtimes
                .FirstOrDefault(runtime => string.Equals(runtime.Profile.ProfileId, profileId, StringComparison.Ordinal))
                ?.Profile;
            if (profile is null)
            {
                return;
            }

            var claim = ChatGptProtectedClaimEvaluator.Evaluate(profile, _storageLayout);
            if (!claim.Protected)
            {
                return;
            }

            var state = current.State with
            {
                NativeSubmitEnabled = current.State.Enabled,
                NativeSubmitStatus = OsInteractionStatusIds.Protected,
                ReadinessStatus = OsInteractionStatusIds.Protected,
                ComposerProtected = true,
                ProtectedClaimStatus = claim.Status,
                ReferenceAcceptanceStatus = claim.ReferenceStatus,
                LiveContractStatus = claim.LiveContractStatus
            };
            if (PublishSnapshotIfCurrent(current, current with { State = state }))
            {
                return;
            }
        }
    }

    private ProtectionSnapshot? PublishTraceUnavailable(ProtectionSnapshot source, string? profileId)
    {
        var current = ReadSnapshot();
        if (!CanContinueWithRuntime(current, source))
        {
            return null;
        }

        PublishNativeSubmitState(
            current,
            OsInteractionStatusIds.TraceUnavailable,
            OsInteractionStatusIds.TraceUnavailable,
            profileId,
            applied: false,
            submitted: false);
        return ReadSnapshot();
    }

    private void PublishStaleCapturedAttempt(
        ProtectionSnapshot capturedSnapshot,
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitTargetIdentity? target,
        string profileId,
        string reason)
    {
        using var operation = new ResidentProtectedSendOperation(
            capturedSnapshot,
            runtimeSet,
            target,
            runtimeSet.CancelActiveSideEffects);
        if (!operation.TryEnsureTerminalBlockedTrace(out var trace)
            || trace.Count == 0)
        {
            return;
        }

        var interruption = new ProtectedSendInterruption(
            operation.AttemptId,
            capturedSnapshot.Generation,
            reason,
            "retry_protection");
        while (true)
        {
            var current = ReadSnapshot();
            if (current.State.ProtectedSendAttemptId > operation.AttemptId
                || (current.State.ProtectedSendAttemptId == operation.AttemptId
                    && current.State.ProtectedSendAttemptTrace is { Count: > 0 }))
            {
                return;
            }

            var enabled = current.State.Enabled;
            var readinessStatus = enabled
                ? OsInteractionStatusIds.TraceUnavailable
                : current.State.ReadinessStatus;
            var replacementState = current.State with
            {
                LastStatus = enabled ? OsInteractionStatusIds.TraceUnavailable : current.State.LastStatus,
                LastProfileId = profileId,
                LastApplied = false,
                LastSubmitted = false,
                NativeSubmitEnabled = enabled ? false : current.State.NativeSubmitEnabled,
                NativeSubmitStatus = enabled ? OsInteractionStatusIds.TraceUnavailable : current.State.NativeSubmitStatus,
                ProtectedSendBinding = ProtectedSendBindingText(current, readinessStatus, profileId),
                ReadinessStatus = readinessStatus,
                ComposerProtected = enabled ? false : current.State.ComposerProtected,
                ProtectedSendAttemptStatus = "trace_unavailable",
                ProtectedSendAttemptAction = "retry_protection",
                ProtectedSendAttemptId = operation.AttemptId,
                ProtectedSendAttemptTrace = trace,
                ProtectedSendAttemptStartedAtTimestamp = operation.StartedAtTimestamp,
                LastProtectedSendInterruption = interruption,
                LastProtectedSendTraceStatus = "trace_unavailable"
            };

            if (PublishSnapshotIfCurrent(current, current with { State = replacementState }))
            {
                return;
            }
        }
    }

    private static int DurationSince(long startTimestamp)
    {
        if (startTimestamp <= 0)
        {
            return 0;
        }

        var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        return (int)Math.Clamp(elapsed, 0, int.MaxValue);
    }

    private static bool CanContinueWithRuntime(ProtectionSnapshot current, ProtectionSnapshot source)
    {
        return current.State.Enabled
            && current.HookReady
            && current.Generation == source.Generation
            && ReferenceEquals(current.RuntimeSet, source.RuntimeSet)
            && IsLocalProtectionReady(current);
    }

    private static bool ActiveAttemptInterruptedByRuntimeReload(TrayProtectionState state)
    {
        return state.ProtectedSendAttemptStatus is "detected" or "checking" or "in_progress"
            && state.ProtectedSendAttemptId > 0;
    }

    private void ObserveProtectedSendStage(string stage)
    {
        _protectedSendStageObserver?.Invoke(stage);
    }

    private TrayProtectionState CarryRuntimeReloadSendState(
        TrayProtectionState candidateState,
        ProtectionSnapshot source,
        ResidentProtectedSendOperation? interruptedOperation = null)
    {
        var activeOperation = interruptedOperation ?? Volatile.Read(ref _activeProtectedSendOperation);
        if (ActiveAttemptInterruptedByRuntimeReload(source.State))
        {
            return CarryInterruptedAttemptState(
                candidateState,
                source.State,
                source.Generation,
                "runtime_replaced",
                activeOperation);
        }

        if (activeOperation is not null
            && ReferenceEquals(activeOperation.RuntimeSet, source.RuntimeSet))
        {
            return CarryUnpublishedAttemptState(
                candidateState,
                activeOperation,
                source.Generation,
                "runtime_replaced");
        }

        return candidateState with
        {
            LastProtectedSendInterruption = source.State.LastProtectedSendInterruption
        };
    }

    private static TrayProtectionState CarryUnpublishedAttemptState(
        TrayProtectionState candidateState,
        ResidentProtectedSendOperation operation,
        long sourceGeneration,
        string reason)
    {
        var interruption = new ProtectedSendInterruption(
            operation.AttemptId,
            sourceGeneration,
            reason,
            "retry_protection");
        operation.TryEnsureTerminalBlockedTrace(out var trace);

        return candidateState with
        {
            ProtectedSendAttemptStatus = "trace_unavailable",
            ProtectedSendAttemptAction = "retry_protection",
            ProtectedSendAttemptId = operation.AttemptId,
            ProtectedSendAttemptTrace = trace.Count == 0 ? null : trace,
            ProtectedSendAttemptStartedAtTimestamp = operation.StartedAtTimestamp,
            LastProtectedSendInterruption = interruption,
            LastProtectedSendTraceStatus = "trace_unavailable"
        };
    }

    private static TrayProtectionState CarryInterruptedAttemptState(
        TrayProtectionState candidateState,
        TrayProtectionState sourceState,
        long sourceGeneration,
        string reason,
        ResidentProtectedSendOperation? operation = null)
    {
        var interruption = new ProtectedSendInterruption(
            operation?.AttemptId ?? sourceState.ProtectedSendAttemptId,
            sourceGeneration,
            reason,
            "retry_protection");
        var trace = operation?.Trace ?? sourceState.ProtectedSendAttemptTrace;
        if (operation is not null)
        {
            operation.TryEnsureTerminalBlockedTrace(out trace);
        }

        return candidateState with
        {
            ProtectedSendAttemptStatus = "trace_unavailable",
            ProtectedSendAttemptAction = "retry_protection",
            ProtectedSendAttemptId = operation?.AttemptId ?? sourceState.ProtectedSendAttemptId,
            ProtectedSendAttemptTrace = operation is null ? null : trace,
            ProtectedSendAttemptStartedAtTimestamp = operation?.StartedAtTimestamp ?? sourceState.ProtectedSendAttemptStartedAtTimestamp,
            LastProtectedSendInterruption = interruption,
            LastProtectedSendTraceStatus = "trace_unavailable"
        };
    }

    private static string ProtectedSendAttemptStatus(string status, bool submitted)
    {
        if (submitted && status == OsInteractionStatusIds.Submitted)
        {
            return "sent_safely";
        }

        return status switch
        {
            OsInteractionStatusIds.NativeSubmitInProgress => "in_progress",
            OsInteractionStatusIds.NativeSubmitSetupRequired => "setup_required",
            OsInteractionStatusIds.ProfilesUnavailable => "settings_unavailable",
            OsInteractionStatusIds.SurfaceUnverified or OsInteractionStatusIds.NotComposer
                or OsInteractionStatusIds.BindingUnknown or OsInteractionStatusIds.NotConfigured => "binding_not_verified",
            OsInteractionStatusIds.FocusLost or OsInteractionStatusIds.StaleComposer => "composer_changed",
            OsInteractionStatusIds.Canceled => "canceled",
            OsInteractionStatusIds.ReplayIndeterminate => "replay_indeterminate",
            OsInteractionStatusIds.TraceUnavailable => "trace_unavailable",
            OsInteractionStatusIds.EnterpriseBlocked => "policy_blocked",
            OsInteractionStatusIds.Blocked => "content_blocked",
            LocalProtectionRecovery.RecoveryRequiredCode or LocalProtectionRecovery.RuntimeDegradedCode => "local_protection_unavailable",
            _ => "protection_unavailable"
        };
    }

    private static string ProtectedSendAttemptAction(string status, bool submitted)
    {
        if (submitted && status == OsInteractionStatusIds.Submitted)
        {
            return "none";
        }

        return status switch
        {
            OsInteractionStatusIds.NativeSubmitSetupRequired => "set_up_prompt_protection",
            OsInteractionStatusIds.ProfilesUnavailable => "repair_profile_settings",
            OsInteractionStatusIds.SurfaceUnverified or OsInteractionStatusIds.NotComposer
                or OsInteractionStatusIds.BindingUnknown or OsInteractionStatusIds.NotConfigured => "set_up_prompt_protection",
            OsInteractionStatusIds.FocusLost or OsInteractionStatusIds.StaleComposer => "focus_and_send_again",
            OsInteractionStatusIds.Canceled => "edit_or_send_again",
            OsInteractionStatusIds.ReplayIndeterminate => "retry_protection",
            OsInteractionStatusIds.TraceUnavailable => "retry_protection",
            OsInteractionStatusIds.NativeSubmitInProgress => "wait_for_current_send",
            OsInteractionStatusIds.EnterpriseBlocked => "contact_administrator",
            OsInteractionStatusIds.Blocked => "edit_prompt_and_send_again",
            LocalProtectionRecovery.RecoveryRequiredCode or LocalProtectionRecovery.RuntimeDegradedCode => "repair_local_protection",
            _ => "retry_protection"
        };
    }

    private static string NativeSubmitReadinessStatusAfterFlow(string flowStatus)
    {
        return flowStatus is OsInteractionStatusIds.DegradedHotkeyOnly
            or OsInteractionStatusIds.EnterpriseBlocked
            or OsInteractionStatusIds.SurfaceUnverified
            or OsInteractionStatusIds.BindingUnknown
            or OsInteractionStatusIds.NotConfigured
            or OsInteractionStatusIds.NativeSubmitSetupRequired
            or OsInteractionStatusIds.ProfilesUnavailable
            or OsInteractionStatusIds.TraceUnavailable
            or OsInteractionStatusIds.FocusLost
            or OsInteractionStatusIds.StaleComposer
            ? flowStatus
            : OsInteractionStatusIds.Protected;
    }

    private static string NativeSubmitFlowStatusForPublication(NativeSubmitInterceptionResult result)
    {
        return result.Status == OsInteractionStatusIds.FailedClosed
            && result.Diagnostics.Keys.Any(key => key == "trace_status")
            ? OsInteractionStatusIds.TraceUnavailable
            : result.Status;
    }

    private void PublishBlockedNativeSubmitState(
        ProtectionSnapshot snapshot,
        NativeSubmitInterceptionResult classification,
        string? profileId)
    {
        var resolvedProfileId = classification.Diagnostics.TryGetValue("profile_id", out var classifiedProfileId)
            ? classifiedProfileId
            : profileId ?? snapshot.State.ConfiguredProfileId;
        var readinessStatus = NativeSubmitReadinessStatusAfterFlow(classification.Status);
        PublishNativeSubmitState(
            snapshot,
            classification.Status,
            readinessStatus,
            resolvedProfileId,
            applied: false,
            submitted: false,
            setupRequired: readinessStatus == OsInteractionStatusIds.NativeSubmitSetupRequired);
    }

    private void PublishUnattributedClassificationFailure(
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitInterceptionResult classification)
    {
        if (!classification.Diagnostics.TryGetValue("classification_completed", out var completion)
            || !string.Equals(completion, "unavailable", StringComparison.Ordinal))
        {
            return;
        }

        var snapshot = ReadSnapshot();
        var activeRuntimeSet = snapshot.RuntimeSet ?? runtimeSet;

        var profileId = classification.Diagnostics.TryGetValue("profile_id", out var classifiedProfileId)
            ? classifiedProfileId
            : activeRuntimeSet.Runtimes.FirstOrDefault()?.Profile.ProfileId
                ?? snapshot.State.ConfiguredProfileId
                ?? "selected_client";
        PublishStaleCapturedAttempt(
            snapshot,
            activeRuntimeSet,
            target: null,
            profileId,
            snapshot.State.Enabled ? "classification_failed" : "protection_stopped");
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
        bool setupRequired = false,
        string localProtectionStatus = LocalProtectionRecovery.ReadyCode)
    {
        var localProtectionReady = string.Equals(
            localProtectionStatus,
            LocalProtectionRecovery.ReadyCode,
            StringComparison.Ordinal);
        var effectiveNativeSubmitStatus = localProtectionReady
            ? nativeSubmitStatus
            : localProtectionStatus;
        var chatGptRuntime = runtimes.FirstOrDefault(runtime =>
            string.Equals(runtime.Profile.ProfileId, "chatgpt-desktop", StringComparison.Ordinal));
        var protectedClaim = chatGptRuntime is null
            ? new ChatGptProtectedClaimResult(
                OsInteractionStatusIds.Protected,
                "not_applicable",
                "not_applicable",
                "not_applicable")
            : ChatGptProtectedClaimEvaluator.Evaluate(chatGptRuntime.Profile, _storageLayout);
        if (effectiveNativeSubmitStatus == OsInteractionStatusIds.Protected
            && chatGptRuntime is not null
            && !protectedClaim.Protected)
        {
            effectiveNativeSubmitStatus = protectedClaim.Status;
        }
        var (projectFilesProtected, projectFileStatus) = ReadProjectFileProtectionStatus();
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
            NativeSubmitEnabled: nativeSubmitEnabled
                && localProtectionReady
                && effectiveNativeSubmitStatus == OsInteractionStatusIds.Protected,
            NativeSubmitStatus: effectiveNativeSubmitStatus,
            ProtectedSendBinding: ProtectedSendBindingText(runtimes, effectiveNativeSubmitStatus),
            NewlineBinding: NewlineBindingText(runtimes),
            ManualScanHotkey: _hotkeyHost.Binding.DisplayText,
            ReadinessStatus: localProtectionReady
                ? ReadinessStatus(runtimes, effectiveNativeSubmitStatus)
                : localProtectionStatus,
            ComposerProtected: localProtectionReady && effectiveNativeSubmitStatus == OsInteractionStatusIds.Protected,
            ProjectFilesProtected: projectFilesProtected,
            ProjectFileStatus: projectFileStatus,
            ResidentProcess: enabled,
            SetupRequired: setupRequired,
            LocalProtectionStatus: localProtectionStatus,
            ConfiguredProfileId: runtimes.FirstOrDefault()?.Profile.ProfileId,
            ProtectedClaimStatus: protectedClaim.Status,
            ReferenceAcceptanceStatus: protectedClaim.ReferenceStatus,
            LiveContractStatus: protectedClaim.LiveContractStatus);
    }

    private (bool ProjectFilesProtected, string ProjectFileStatus) ReadProjectFileProtectionStatus()
    {
        var projectFileStatus = ProjectFileProtectionStatusInspector.Inspect(_storageLayout);
        return (
            ProjectFilesProtected: projectFileStatus == ProjectFileProtectionStatusValues.Protected,
            ProjectFileStatus: projectFileStatus);
    }

    private ChatGptProtectedClaimResult ReadResidentChatGptClaim()
    {
        var state = ReadSnapshot().State;
        return new ChatGptProtectedClaimResult(
            state.ProtectedClaimStatus,
            state.ReferenceAcceptanceStatus,
            state.LiveContractStatus,
            "resident_snapshot");
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
        string nativeSubmitStatus,
        string? profileId = null)
    {
        var runtimes = snapshot.RuntimeSet?.Runtimes ?? Array.Empty<NativeSubmitRuntime>();
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            var matchedRuntime = runtimes.FirstOrDefault(runtime => string.Equals(
                runtime.Profile.ProfileId,
                profileId,
                StringComparison.Ordinal));
            if (nativeSubmitStatus == OsInteractionStatusIds.Protected && matchedRuntime?.Profile.SubmitBinding is not null)
            {
                return matchedRuntime.Profile.SubmitBinding.DisplayText;
            }
        }

        return ProtectedSendBindingText(runtimes, nativeSubmitStatus);
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
        if (nativeSubmitStatus == OsInteractionStatusIds.ProfilesUnavailable)
        {
            return nativeSubmitStatus;
        }

        var primaryRuntime = runtimes.FirstOrDefault();
        if (primaryRuntime is null)
        {
            return OsInteractionStatusIds.NotConfigured;
        }

        return nativeSubmitStatus == OsInteractionStatusIds.Protected
            ? OsInteractionStatusIds.Protected
            : primaryRuntime.Profile.CapabilityStatus;
    }

    private NativeSubmitInterceptionResult ClassifyNativeGesture(
        NativeSubmitRuntimeSet runtimeSet,
        NativeKeyGesture gesture)
    {
        var snapshot = ReadSnapshot();
        var discovery = DiscoverActiveSurface(snapshot);
        RememberCapturedTargetProfile(gesture.TargetWindow, gesture.TargetProcessId, discovery);
        var focusedControlResult = ClassifyFocusedSendControl(
            snapshot,
            runtimeSet,
            discovery,
            gesture,
            out var focusedComposerDiscovery);
        if (focusedControlResult is not null)
        {
            var focusedRuntime = ResolveRuntime(snapshot, runtimeSet, focusedComposerDiscovery)
                ?? ResolveRuntimeByProfileIdentity(snapshot, runtimeSet, focusedComposerDiscovery ?? discovery);
            return RememberSnapshot(
                snapshot,
                runtimeSet,
                !IsLocalProtectionReady(snapshot)
                    && focusedControlResult.SuppressOriginalInput
                    && focusedRuntime is not null
                    ? SuppressLocalProtectionRecoverySubmit(focusedRuntime.Profile.ProfileId)
                    : focusedControlResult,
                NativeSubmitTargetIdentity.TryCreateForGesture(
                    snapshot.Generation,
                    focusedComposerDiscovery?.Surface,
                    gesture.TargetWindow));
        }

        var runtime = ResolveRuntime(snapshot, runtimeSet, discovery);
        var knownDiscovery = discovery.Succeeded || HasProfileIdentity(discovery)
            ? discovery
            : null;
        runtime ??= ResolveRuntimeByProfileIdentity(snapshot, runtimeSet, discovery);
        if (!IsLocalProtectionReady(snapshot)
            && runtime is not null
            && (discovery.Succeeded || HasProfileIdentity(discovery))
            && runtime.Profile.SubmitBinding?.Matches(gesture) == true)
        {
            return RememberSnapshot(
                snapshot,
                runtimeSet,
                SuppressLocalProtectionRecoverySubmit(runtime.Profile.ProfileId),
                NativeSubmitTargetIdentity.TryCreateForGesture(
                    snapshot.Generation,
                    discovery.Surface,
                    gesture.TargetWindow));
        }

        var result = runtime is null
            ? (runtimeSet.Runtimes.Count > 1
                ? PassThroughPointer()
                : runtimeSet.Runtimes.FirstOrDefault()?.Controller.HandleGesture(
                    gesture,
                    activeSurfaceDiscovery: knownDiscovery) ?? PassThroughPointer())
            : runtime.Controller.HandleGesture(gesture, activeSurfaceDiscovery: knownDiscovery);
        return RememberSnapshot(
            snapshot,
            runtimeSet,
            result,
            NativeSubmitTargetIdentity.TryCreateForGesture(
                snapshot.Generation,
                discovery.Surface,
                gesture.TargetWindow));
    }

    private NativeSubmitInterceptionResult? ClassifyFocusedSendControl(
        ProtectionSnapshot snapshot,
        NativeSubmitRuntimeSet runtimeSet,
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
        var runtime = ResolveRuntime(snapshot, runtimeSet, focusedControl.ComposerDiscovery)
            ?? ResolveRuntimeByProfileIdentity(snapshot, runtimeSet, focusedControl.ComposerDiscovery);
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
        NativeSubmitRuntimeSet? runtimeSetOverride = null,
        TextSurfaceDiscoveryResult? knownSurface = null)
    {
        var runtimeSet = runtimeSetOverride ?? snapshot.RuntimeSet;
        if (runtimeSet is null)
        {
            return null;
        }

        if (knownSurface is not null)
        {
            if (!knownSurface.Succeeded || knownSurface.Surface is null)
            {
                return null;
            }

            return runtimeSet.Runtimes.FirstOrDefault(runtime => string.Equals(
                runtime.Profile.ProfileId,
                knownSurface.Surface.ProfileId,
                StringComparison.Ordinal));
        }

        if (runtimeSet.Runtimes.Count == 1)
        {
            return runtimeSet.Runtimes[0];
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

        return runtimeSet.Runtimes.FirstOrDefault(runtime => string.Equals(
            runtime.Profile.ProfileId,
            discovery.Surface.ProfileId,
            StringComparison.Ordinal));
    }

    private static NativeSubmitRuntime? ResolveRuntimeByProfileIdentity(
        ProtectionSnapshot snapshot,
        NativeSubmitRuntimeSet? runtimeSetOverride,
        TextSurfaceDiscoveryResult discovery)
    {
        var profileId = discovery.Surface?.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            discovery.Diagnostics.TryGetValue("profile_id", out profileId);
        }

        var runtimeSet = runtimeSetOverride ?? snapshot.RuntimeSet;
        return string.IsNullOrWhiteSpace(profileId)
            ? null
            : runtimeSet?.Runtimes.FirstOrDefault(runtime => string.Equals(
                runtime.Profile.ProfileId,
                profileId,
                StringComparison.Ordinal));
    }

    private static bool HasProfileIdentity(TextSurfaceDiscoveryResult discovery)
    {
        return !string.IsNullOrWhiteSpace(discovery.Surface?.ProfileId)
            || discovery.Diagnostics.ContainsKey("profile_id");
    }

    private static bool IsLocalProtectionReady(ProtectionSnapshot snapshot)
    {
        return string.Equals(
            snapshot.State.LocalProtectionStatus,
            LocalProtectionRecovery.ReadyCode,
            StringComparison.Ordinal);
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
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitInterceptionResult classification)
    {
        if (!classification.Diagnostics.TryGetValue("profile_id", out var profileId))
        {
            return null;
        }

        return runtimeSet.Runtimes.FirstOrDefault(runtime => string.Equals(
            runtime.Profile.ProfileId,
            profileId,
            StringComparison.Ordinal));
    }

    private SetupReadiness ReadSelectedProfileSetupReadiness(NativeSubmitRuntimeSet runtimeSet)
    {
        var setupRequired = false;
        foreach (var runtime in runtimeSet.Runtimes)
        {
            var status = runtime.Controller.GetSetupReadinessStatus(
                _storageLayout,
                runtime.Profile.ProfileId);
            if (status == OsInteractionStatusIds.ProfilesUnavailable)
            {
                return new SetupReadiness(
                    SetupRequired: false,
                    Status: OsInteractionStatusIds.ProfilesUnavailable);
            }

            if (status == OsInteractionStatusIds.NativeSubmitSetupRequired)
            {
                setupRequired = true;
            }

            if (status == OsInteractionStatusIds.Protected
                && string.Equals(runtime.Profile.ProfileId, "chatgpt-desktop", StringComparison.Ordinal))
            {
                var claim = ChatGptProtectedClaimEvaluator.Evaluate(runtime.Profile, _storageLayout);
                if (!claim.Protected)
                {
                    return new SetupReadiness(
                        SetupRequired: false,
                        Status: claim.Status);
                }
            }
        }

        return new SetupReadiness(
            SetupRequired: setupRequired,
            Status: setupRequired
                ? OsInteractionStatusIds.NativeSubmitSetupRequired
                : OsInteractionStatusIds.Protected);
    }

    private ProtectionSnapshot ReadSnapshot()
    {
        return Volatile.Read(ref _currentSnapshot);
    }

    private void PublishSnapshot(ProtectionSnapshot snapshot)
    {
        Volatile.Write(ref _currentSnapshot, snapshot);
        NotifyStateChanged();
    }

    private bool PublishSnapshotIfCurrent(ProtectionSnapshot expected, ProtectionSnapshot replacement)
    {
        if (!TryReplaceSnapshotIfCurrent(expected, replacement))
        {
            return false;
        }

        NotifyStateChanged();
        return true;
    }

    private bool TryReplaceSnapshotIfCurrent(ProtectionSnapshot expected, ProtectionSnapshot replacement)
    {
        return ReferenceEquals(
            Interlocked.CompareExchange(ref _currentSnapshot, replacement, expected),
            expected);
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool HasRequiredResidentTraceRunner(NativeSubmitRuntime runtime)
    {
        return runtime.ResidentTargetTracedRunner is not null
            || runtime.TestOnlyUntargetedRunner is not null;
    }

    private static void StopAndDisposeRuntime(NativeSubmitRuntimeSet? runtimeSet)
    {
        if (runtimeSet is null)
        {
            return;
        }

        runtimeSet.HookHost.Stop();
        runtimeSet.Dispose();
    }

    private NativeSubmitInterceptionResult RememberSnapshot(
        ProtectionSnapshot snapshot,
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitInterceptionResult classification,
        NativeSubmitTargetIdentity? target = null)
    {
        _classificationSnapshots.Add(classification, new NativeSubmitExecutionContext(snapshot, runtimeSet, target));
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

    private bool TryBeginProtectedSendOperation(
        ProtectionSnapshot snapshot,
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitTargetIdentity? target,
        out ResidentProtectedSendOperation operation)
    {
        lock (_reloadGate)
        {
            var candidate = new ResidentProtectedSendOperation(
                snapshot,
                runtimeSet,
                target,
                runtimeSet.CancelActiveSideEffects);
            if (Interlocked.CompareExchange(ref _activeProtectedSendOperation, candidate, null) is not null)
            {
                candidate.Dispose();
                operation = null!;
                return false;
            }

            if (!candidate.CanContinue(ReadSnapshot()))
            {
                CompleteProtectedSendOperation(candidate);
                operation = null!;
                return false;
            }

            operation = candidate;
            return true;
        }
    }

    private bool TryRunProtectedSendOperation<T>(
        ProtectionSnapshot snapshot,
        NativeSubmitRuntimeSet runtimeSet,
        NativeSubmitTargetIdentity? target,
        Func<ResidentProtectedSendOperation, T> runner,
        out T result)
    {
        result = default!;
        if (!TryBeginProtectedSendOperation(snapshot, runtimeSet, target, out var operation))
        {
            return false;
        }

        operation.MarkExecutionStarted();
        try
        {
            ObserveProtectedSendStage("operation_started");
            result = runner(operation);
            return true;
        }
        finally
        {
            CompleteProtectedSendOperation(operation);
        }
    }

    private bool CanContinueProtectedSendOperation(ResidentProtectedSendOperation operation)
    {
        return operation.CanContinue(ReadSnapshot());
    }

    private IDisposable? AcquireProtectedSendSideEffect(ResidentProtectedSendOperation operation)
    {
        return operation.TryAcquireSideEffect(ReadSnapshot());
    }

    private void CompleteProtectedSendOperation(ResidentProtectedSendOperation operation)
    {
        try
        {
            PersistProtectedSendOperationTrace(operation);
        }
        finally
        {
            operation.TryComplete();
            Interlocked.CompareExchange(ref _activeProtectedSendOperation, null, operation);
            operation.Dispose();
        }
    }

    private void PersistProtectedSendOperationTrace(ResidentProtectedSendOperation operation)
    {
        var current = ReadSnapshot();
        if (!ReferenceEquals(current.RuntimeSet, operation.RuntimeSet)
            || current.Generation != operation.Snapshot.Generation)
        {
            return;
        }

        var trace = operation.Trace;
        if (trace.Count > 0
            && trace[^1].Stage is "terminal_blocked" or "sent_safely"
            && current.State.ProtectedSendAttemptId == operation.AttemptId
            && current.State.ProtectedSendAttemptTrace is { Count: var currentTraceCount }
            && currentTraceCount >= trace.Count)
        {
            return;
        }

        _ = PublishTerminalBlockedTrace(operation.Snapshot, operation);
    }

    private void CancelAndDrainActiveProtectedSendOperation(NativeSubmitRuntimeSet? runtimeSet)
    {
        var operation = Volatile.Read(ref _activeProtectedSendOperation);
        if (operation is not null
            && (runtimeSet is null || ReferenceEquals(operation.RuntimeSet, runtimeSet)))
        {
            operation.Cancel();
            _ = operation.WaitForCompletion(TimeSpan.FromSeconds(5));
        }
    }

    private static OsInteractionResult RunNativeSubmitFlow(
        NativeSubmitRuntime runtime,
        NativeSubmitTargetIdentity? target,
        Func<string, string, bool> traceStage,
        Func<bool> executionGuard,
        Func<IDisposable?> executionLease)
    {
        if (runtime.ResidentTargetTracedRunner is not null
            && target is not null
            && string.Equals(target.ProfileId, runtime.Profile.ProfileId, StringComparison.Ordinal))
        {
            return runtime.ResidentTargetTracedRunner(target, traceStage, executionGuard, executionLease);
        }

        if (runtime.TestOnlyUntargetedRunner is not null && target is null)
        {
            return runtime.TestOnlyUntargetedRunner(traceStage, executionGuard, executionLease);
        }

        return TraceRunnerUnavailableResult();
    }

    private static NativeSubmitInterceptionResult FailedClosedNativeSubmitResult()
    {
        return new NativeSubmitInterceptionResult(
            OsInteractionStatusIds.FailedClosed,
            SuppressOriginalInput: true,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["trace_status"] = "trace_unavailable"
            });
    }

    internal static OsInteractionResult TraceRunnerUnavailableResult()
    {
        return new OsInteractionResult(
            OsInteractionStatusIds.TraceUnavailable,
            Surface: null,
            SanitizationResult: null,
            ConfirmationModel: null,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["trace_status"] = "trace_unavailable"
            });
    }

}

internal sealed record NativeSubmitRuntime(
    INativeSubmitHookHost HookHost,
    NativeSubmitInterceptionController Controller,
    SubmitBindingProfile Profile,
    Func<Func<string, string, bool>, Func<bool>, Func<IDisposable?>, OsInteractionResult>? ResidentTracedRunner = null,
    Func<NativeSubmitTargetIdentity, Func<string, string, bool>, Func<bool>, Func<IDisposable?>, OsInteractionResult>? ResidentTargetTracedRunner = null,
    Func<Func<string, string, bool>, Func<bool>, Func<IDisposable?>, OsInteractionResult>? TestOnlyUntargetedRunner = null)
{
    public static NativeSubmitRuntime CreateTest(
        INativeSubmitHookHost hookHost,
        NativeSubmitInterceptionController controller,
        Func<OsInteractionResult> runner,
        SubmitBindingProfile profile,
        Func<Func<string, string, bool>, Func<bool>, Func<IDisposable?>, OsInteractionResult>? ResidentTracedRunner = null,
        Func<NativeSubmitTargetIdentity, Func<string, string, bool>, Func<bool>, Func<IDisposable?>, OsInteractionResult>? ResidentTargetTracedRunner = null)
    {
        return new NativeSubmitRuntime(
            hookHost,
            controller,
            profile,
            ResidentTracedRunner: ResidentTracedRunner,
            ResidentTargetTracedRunner ?? ((target, traceStage, executionGuard, executionLease) =>
                RunTestTracedRunner(runner, traceStage, executionGuard, executionLease)),
            TestOnlyUntargetedRunner: ResidentTracedRunner ?? ((traceStage, executionGuard, executionLease) =>
                RunTestTracedRunner(runner, traceStage, executionGuard, executionLease)));
    }

    private static OsInteractionResult RunTestTracedRunner(
        Func<OsInteractionResult> runner,
        Func<string, string, bool> traceStage,
        Func<bool> executionGuard,
        Func<IDisposable?> executionLease)
    {
        foreach (var stage in new[]
        {
            (Stage: "composer_read", Code: "capture_verified"),
            (Stage: "sanitized", Code: "sanitization_verified"),
            (Stage: "send_injected", Code: "submit_requested")
        })
        {
            if (!traceStage(stage.Stage, stage.Code))
            {
                return TraceUnavailableResult("test_trace_unavailable");
            }
        }

        if (!executionGuard())
        {
            return TraceUnavailableResult("resident_operation_unavailable");
        }

        var lease = executionLease();
        if (lease is null)
        {
            return TraceUnavailableResult("resident_operation_unavailable");
        }

        try
        {
            return runner();
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static OsInteractionResult TraceUnavailableResult(string status)
    {
        return new OsInteractionResult(
            OsInteractionStatusIds.FailedClosed,
            Surface: null,
            SanitizationResult: null,
            ConfirmationModel: null,
            Applied: false,
            Submitted: false,
            Diagnostics: new Dictionary<string, string>
            {
                ["trace_status"] = status
            });
    }
}

internal sealed record NativeSubmitRuntimeSet(
    INativeSubmitHookHost HookHost,
    IReadOnlyList<NativeSubmitRuntime> Runtimes,
    IDisposable? ResourceOwner = null,
    Action? CancelActiveSideEffects = null) : IDisposable
{
    public void Dispose()
    {
        ResourceOwner?.Dispose();
    }
}

internal sealed record ResidentProtectionRuntime(
    Func<OsInteractionResult> ApplyOnlyRunner,
    NativeSubmitRuntimeSet? NativeSubmitRuntimeSet,
    IDisposable? ApplyOnlyResourceOwner = null);

internal static class TrayStatusFormatter
{
    public static string FormatMenuStatus(TrayProtectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var enabled = state.Enabled ? "enabled" : "disabled";
        var replacements = state.LastReplacementCount is null
            ? "n/a"
            : state.LastReplacementCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"status={enabled} mode={DisplayMode(state.Mode)} composer_protected={state.ComposerProtected.ToString().ToLowerInvariant()} protected_claim={DisplayProofStatus(state.ProtectedClaimStatus)} reference_acceptance={DisplayProofStatus(state.ReferenceAcceptanceStatus)} live_contract={DisplayProofStatus(state.LiveContractStatus)} programmatic_uia_invoke={DisplayStatus(state.ProgrammaticUiaInvokeStatus)} project_files_protected={state.ProjectFilesProtected.ToString().ToLowerInvariant()} project_file_status={DisplayProjectFileStatus(state.ProjectFileStatus)} protected_send_binding={DisplayBinding(state.ProtectedSendBinding)} newline_binding={DisplayBinding(state.NewlineBinding)} manual_scan_hotkey={DisplayManualHotkey(state.ManualScanHotkey)} protected_send_attempt={DisplayProtectedSendAttempt(state.ProtectedSendAttemptStatus)} attempt_action={DisplayProtectedSendAttemptAction(state.ProtectedSendAttemptAction)} protected_send_interruption={DisplayProtectedSendInterruption(state.LastProtectedSendInterruption)} native_submit={DisplayStatus(state.NativeSubmitStatus)} readiness={DisplayStatus(state.ReadinessStatus)} last={DisplayStatus(state.LastStatus)} replacements={replacements}";
    }

    public static string FormatNotifyIconText(TrayProtectionState state, string? buildVersion = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var enabled = state.Enabled ? "enabled" : "disabled";
        var product = string.IsNullOrWhiteSpace(buildVersion)
            ? "CodexRG"
            : $"CodexRG {buildVersion}";
        return TrimNotifyText($"{product} {enabled} {DisplayMode(state.Mode)} last={DisplayStatus(state.LastStatus)} send={DisplayBinding(state.ProtectedSendBinding)} manual={DisplayManualHotkey(state.ManualScanHotkey)}");
    }

    public static string FormatRecoveryRequiredNotifyIconText(string localProtectionStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localProtectionStatus);

        return TrimNotifyText($"CodexRG local_protection={LocalProtectionRecovery.ToSafeStatusCode(localProtectionStatus)}");
    }

    public static string FormatStartupError(TrayProtectionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return $"Protection disabled. manual_scan_hotkey={DisplayManualHotkey(state.ManualScanHotkey)} protected_send_binding={DisplayBinding(state.ProtectedSendBinding)} readiness={DisplayStatus(state.ReadinessStatus)} error={DisplayStatus(state.LastStatus)}";
    }

    private static string DisplayMode(string value)
    {
        return value is "ApplyOnly" or "NativeSubmit" ? value : "unavailable";
    }

    private static string DisplayBinding(string value)
    {
        return value is "Enter" or "Ctrl+Enter" or "not_configured" or "unknown"
            ? value
            : "unknown";
    }

    private static string DisplayManualHotkey(string value)
    {
        return value is "Ctrl+Shift+F9" or "Ctrl+Enter" or "not_configured"
            ? value
            : "configured";
    }

    private static string DisplayProjectFileStatus(string value)
    {
        return ProjectFileProtectionStatusValues.ToSafeDisplayValue(value);
    }

    private static string DisplayProtectedSendAttempt(string value)
    {
        return value is "idle" or "detected" or "checking" or "in_progress" or "sent_safely"
            or "setup_required" or "binding_not_verified" or "composer_changed" or "canceled"
            or "local_protection_unavailable" or "policy_blocked" or "protection_unavailable"
            or "content_blocked" or "trace_unavailable" or "settings_unavailable"
            or "replay_indeterminate"
            ? value
            : "unavailable";
    }

    private static string DisplayProtectedSendAttemptAction(string value)
    {
        return value is "none" or "checking_prompt" or "wait_for_current_send" or "set_up_prompt_protection"
            or "focus_and_send_again" or "edit_or_send_again" or "repair_local_protection"
            or "contact_administrator" or "edit_prompt_and_send_again" or "retry_protection"
            ? value
            : "none";
    }

    private static string DisplayStatus(string value)
    {
        return value switch
        {
            "enabled" or "disabled" or "enabled_native_submit_manual_hotkey_unavailable" or "degraded"
                or "native_submit_runtime_reloaded" => value,
            OsInteractionStatusIds.SupportedSurface or OsInteractionStatusIds.UnsupportedSurface
                or OsInteractionStatusIds.UnsupportedPlatform or OsInteractionStatusIds.AmbiguousSurface
                or OsInteractionStatusIds.CaptureFailed or OsInteractionStatusIds.WriteFailed
                or OsInteractionStatusIds.SubmitFailed or OsInteractionStatusIds.VerificationFailed
                or OsInteractionStatusIds.FocusLost or OsInteractionStatusIds.StaleComposer
                or OsInteractionStatusIds.DryRunAllow or OsInteractionStatusIds.DryRunConfirm
                or OsInteractionStatusIds.Blocked or OsInteractionStatusIds.Canceled
                or OsInteractionStatusIds.Applied or OsInteractionStatusIds.Submitted
                or OsInteractionStatusIds.FailedClosed or OsInteractionStatusIds.SafetyDisabled
                or OsInteractionStatusIds.NotComposer or OsInteractionStatusIds.SupportedComposer
                or OsInteractionStatusIds.EvidenceMissing or OsInteractionStatusIds.Protected
                or OsInteractionStatusIds.NotConfigured or OsInteractionStatusIds.BindingUnknown
                or OsInteractionStatusIds.SurfaceUnverified or OsInteractionStatusIds.DegradedHotkeyOnly
                or OsInteractionStatusIds.NativeSubmitGuarded or OsInteractionStatusIds.NativeSubmitInProgress
                or OsInteractionStatusIds.NativeSubmitPassThrough or OsInteractionStatusIds.NativeSubmitCrashed
                or OsInteractionStatusIds.EmergencyDisabled or OsInteractionStatusIds.EnterpriseBlocked
                or OsInteractionStatusIds.NativeSubmitSetupRequired or OsInteractionStatusIds.TraceUnavailable
                or OsInteractionStatusIds.ProfilesUnavailable
                or OsInteractionStatusIds.ProgrammaticUiaInvokeUnsupported
                or OsInteractionStatusIds.ReplayIndeterminate
                or LocalProtectionRecovery.ReadyCode or LocalProtectionRecovery.RecoveryRequiredCode
                or LocalProtectionRecovery.ConfirmationRequiredCode or LocalProtectionRecovery.RecoveredCode
                or LocalProtectionRecovery.RecoveryFailedCode or LocalProtectionRecovery.RecoveryNotRequiredCode
                or LocalProtectionRecovery.RuntimeDegradedCode or LocalProtectionRecovery.ReloadingCode
                or LocalProtectionRecovery.UnavailableCode => value,
            _ => "unavailable"
        };
    }

    private static string DisplayProofStatus(string value)
    {
        return value is "protected" or "degraded" or "missing" or "passed" or "failed" or "mismatch" or "not_applicable"
            ? value
            : "unavailable";
    }

    private static string DisplayProtectedSendInterruption(ProtectedSendInterruption? interruption)
    {
        return interruption is { Reason: "runtime_replaced" or "protection_stopped", Action: "retry_protection" }
            ? interruption.Reason
            : interruption is null
                ? "none"
                : "unavailable";
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
