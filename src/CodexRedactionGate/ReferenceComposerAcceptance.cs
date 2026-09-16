using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal enum ReferenceComposerDecision
{
    Approve,
    Cancel
}

internal enum ReferenceComposerForegroundMode
{
    Verified,
    Refused
}

internal enum ReferenceComposerTargetChangeMode
{
    None,
    BeforeWrite,
    BeforeReplay
}

internal enum ReferenceComposerWriteMode
{
    Available,
    Unavailable
}

internal enum ReferenceComposerReplayMode
{
    Available,
    Unavailable,
    Partial
}

/// <summary>
/// Selects whether the reference acceptance path is allowed to use the
/// production composer access adapter. <see cref="Unavailable"/> simulates a
/// missing or failing production access so that no fixture-side fallback can
/// substitute a successful send.
/// </summary>
internal enum ReferenceComposerAccessMode
{
    Available,
    Unavailable
}

/// <summary>
/// Typed evidence level of a reference acceptance run. Only
/// <see cref="ReferenceProductionAccess"/> may be published as reference
/// production-access proof; it never claims installed or live ChatGPT proof.
/// </summary>
internal enum ReferenceComposerEvidenceLevel
{
    Unavailable = 0,
    ReferenceProductionAccess = 1
}

internal static class ReferenceComposerEvidenceLevelTokens
{
    public const string ReferenceProductionAccessToken = "reference_production_access";

    public const string UnavailableToken = "unavailable";

    public const string RequiredToken = ReferenceProductionAccessToken;

    public static string ToToken(ReferenceComposerEvidenceLevel level)
        => level == ReferenceComposerEvidenceLevel.ReferenceProductionAccess
            ? ReferenceProductionAccessToken
            : UnavailableToken;

    public static bool TryValidate(ReferenceComposerEvidenceLevel level)
        => level == ReferenceComposerEvidenceLevel.ReferenceProductionAccess;

    public static bool TryParse(string? token, out ReferenceComposerEvidenceLevel level)
    {
        if (string.Equals(token, ReferenceProductionAccessToken, StringComparison.Ordinal))
        {
            level = ReferenceComposerEvidenceLevel.ReferenceProductionAccess;
            return true;
        }

        level = ReferenceComposerEvidenceLevel.Unavailable;
        return false;
    }

    /// <summary>
    /// Narrows a raw evidence level to the single level this release is allowed
    /// to publish. Any other value (missing, wrong or stronger than reference
    /// production access, e.g. live / installed-application) is downgraded to
    /// <see cref="ReferenceComposerEvidenceLevel.Unavailable"/> so that it can
    /// never be rendered as installed or live ChatGPT proof.
    /// </summary>
    public static ReferenceComposerEvidenceLevel SelectForPublication(ReferenceComposerEvidenceLevel level)
        => TryValidate(level)
            ? ReferenceComposerEvidenceLevel.ReferenceProductionAccess
            : ReferenceComposerEvidenceLevel.Unavailable;
}

internal sealed record ReferenceComposerAcceptanceReport(
    bool HookStarted,
    bool OriginalInputSuppressed,
    string TerminalStatus,
    bool Submitted,
    IReadOnlyList<string> SentTexts,
    IReadOnlyList<ProtectedSendTraceEntry> Trace,
    IReadOnlyDictionary<string, string> ReplayDiagnostics,
    bool CleanupPassed,
    ReferenceComposerEvidenceLevel EvidenceLevel)
{
    /// <summary>
    /// The only evidence level this release may publish. A report that does not
    /// carry <see cref="ReferenceComposerEvidenceLevel.ReferenceProductionAccess"/>
    /// is not release proof and must be treated as a fail-closed outcome.
    /// </summary>
    public bool HasPublishableEvidence => ReferenceComposerEvidenceLevelTokens.TryValidate(EvidenceLevel);
}

internal sealed record ReferenceComposerAcceptanceSmokeReport(
    bool SafePromptPassed,
    bool SensitivePromptPassed,
    bool CancellationPassed,
    bool RepeatedCleanupPassed,
    string Status)
{
    public bool Passed => SafePromptPassed
        && SensitivePromptPassed
        && CancellationPassed
        && RepeatedCleanupPassed;
}

internal sealed record ReferenceComposerTransactionSnapshot(
    string Status,
    bool Submitted,
    ReferenceComposerEvidenceLevel EvidenceLevel,
    IReadOnlyDictionary<string, string> Diagnostics);

internal sealed class ReferenceComposerPersistentFixtureLifecycle
{
    private bool _activeLease;
    private bool _disposed;
    private bool _initialFocusReady;
    private bool _leaseFocusReacquisitionRecorded;
    private int _scenarioSentCount;
    private string _scenarioState = "none";

    internal ReferenceComposerPersistentFixtureLifecycle(
        int uiThreadId,
        long formIdentity,
        long controlIdentity)
    {
        UiThreadId = uiThreadId;
        FormIdentity = formIdentity;
        ControlIdentity = controlIdentity;
    }

    internal int UiThreadId { get; }
    internal long FormIdentity { get; }
    internal long ControlIdentity { get; }
    internal int InitialFocusHandoffCount { get; private set; }
    internal int LeaseCount { get; private set; }
    internal int LeaseFocusReacquisitionCount { get; private set; }
    internal int CompletedLeaseCount { get; private set; }
    internal bool AllLeaseCleanupPassed { get; private set; } = true;
    internal bool FinalCleanupPassed { get; private set; }
    internal int ScenarioSentCount => _scenarioSentCount;
    internal string ScenarioState => _scenarioState;

    internal void RecordInitialFocus(bool foregroundReady, bool focusedElementReady)
    {
        if (_disposed || InitialFocusHandoffCount != 0)
        {
            throw new InvalidOperationException("Initial fixture focus handoff is single-use.");
        }

        InitialFocusHandoffCount = 1;
        _initialFocusReady = foregroundReady && focusedElementReady;
    }

    internal bool TryBeginLease(
        int uiThreadId,
        long formIdentity,
        long controlIdentity)
    {
        if (_disposed
            || !_initialFocusReady
            || _activeLease
            || uiThreadId != UiThreadId
            || formIdentity != FormIdentity
            || controlIdentity != ControlIdentity)
        {
            return false;
        }

        _activeLease = true;
        _leaseFocusReacquisitionRecorded = false;
        LeaseCount++;
        _scenarioSentCount = 0;
        _scenarioState = "reset";
        return true;
    }

    internal bool TryRecordLeaseFocusReacquisition(
        int uiThreadId,
        long formIdentity,
        long controlIdentity)
    {
        if (_disposed
            || !_activeLease
            || _leaseFocusReacquisitionRecorded
            || uiThreadId != UiThreadId
            || formIdentity != FormIdentity
            || controlIdentity != ControlIdentity)
        {
            return false;
        }

        _leaseFocusReacquisitionRecorded = true;
        LeaseFocusReacquisitionCount++;
        return true;
    }

    internal void RecordScenarioState(int sentCount, string state)
    {
        if (!_activeLease)
        {
            throw new InvalidOperationException("No active fixture lease.");
        }

        _scenarioSentCount = sentCount;
        _scenarioState = state;
    }

    internal void CompleteLease(bool cleanupPassed)
    {
        if (!_activeLease)
        {
            throw new InvalidOperationException("No active fixture lease.");
        }

        _activeLease = false;
        CompletedLeaseCount++;
        AllLeaseCleanupPassed &= cleanupPassed;
    }

    internal void Dispose(bool finalCleanupPassed)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FinalCleanupPassed = !_activeLease
            && AllLeaseCleanupPassed
            && finalCleanupPassed;
    }
}

internal static class ReferenceComposerAcceptanceCompletion
{
    internal static ReferenceComposerEvidenceLevel SelectEvidenceLevel(
        ReferenceComposerTransactionSnapshot transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return ReferenceComposerEvidenceLevelTokens.SelectForPublication(transaction.EvidenceLevel);
    }

    internal static bool IsConsistent(
        ReferenceComposerTransactionSnapshot transaction,
        string stateStatus,
        bool stateSubmitted,
        string terminalStage,
        int sentCount)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!string.Equals(stateStatus, transaction.Status, StringComparison.Ordinal)
            || stateSubmitted != transaction.Submitted)
        {
            return false;
        }

        return transaction.Submitted
            ? terminalStage == "sent_safely" && sentCount == 1
            : terminalStage != "sent_safely";
    }
}

/// <summary>
/// Local-only acceptance fixture. Its input capability is compiled into the
/// hook host and cannot be persisted or selected as an AI client profile.
/// </summary>
internal static class ReferenceComposerAcceptanceRunner
{
    private static readonly TimeSpan StimulusReadinessTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FixtureFocusPreconditionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransactionCompletionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AcceptanceCoherenceTimeout = TimeSpan.FromSeconds(2);

    internal static ReferenceComposerAcceptanceReport Run(
        ISanitizer sanitizer,
        string prompt,
        ReferenceComposerDecision decision,
        ReferenceComposerForegroundMode foregroundMode = ReferenceComposerForegroundMode.Verified,
        ReferenceComposerTargetChangeMode targetChangeMode = ReferenceComposerTargetChangeMode.None,
        ReferenceComposerWriteMode writeMode = ReferenceComposerWriteMode.Available,
        ReferenceComposerReplayMode replayMode = ReferenceComposerReplayMode.Available,
        Func<IClipboardAccessBoundary>? clipboardBoundaryFactory = null,
        ReferenceComposerAccessMode composerAccessMode = ReferenceComposerAccessMode.Available)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(prompt);

        if (!OperatingSystem.IsWindows())
        {
            return new ReferenceComposerAcceptanceReport(
                HookStarted: false,
                OriginalInputSuppressed: false,
                TerminalStatus: OsInteractionStatusIds.UnsupportedPlatform,
                Submitted: false,
                SentTexts: Array.Empty<string>(),
                Trace: Array.Empty<ProtectedSendTraceEntry>(),
                ReplayDiagnostics: new Dictionary<string, string>(),
                CleanupPassed: false,
                EvidenceLevel: ReferenceComposerEvidenceLevel.Unavailable);
        }

        // Fixture seeding is Arrange-only: it writes the reference TextBox on
        // its UI thread before native Enter dispatch. The readiness probe proves
        // ordering, while only production native capture may establish access
        // or evidence; the fixture never supplies text to the transaction.
        ReferenceComposerAcceptanceReport? report = null;
        Exception? failure = null;
        Action? abort = null;
        Func<bool>? cleanupProbe = null;
        string lastAcceptanceStatus = "not_started";
        string lastAcceptanceTraceStage = "none";
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.OleRequired();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var composer = new ReferenceComposerForm();
                using var replacementComposer = new ReferenceComposerForm();
                var targets = new ReferenceComposerTargetController(composer, replacementComposer, targetChangeMode);
                var discovery = new ReferenceComposerSurfaceDiscovery(targets.GetActiveForm);
                var profile = CreateProfile();
                var replay = new ReferenceComposerReplayBoundary(composer, replayMode);
                ReferenceComposerTransactionSnapshot? transactionSnapshot = null;
                var reportFinalized = 0;
                System.Threading.Timer? coherenceDeadline = null;
                var hookHost = new WindowsNativeSubmitHookHost(new[] { profile });
                using var overlay = new WindowsConfirmationOverlay(
                    window =>
                    {
                        if (decision == ReferenceComposerDecision.Approve)
                        {
                            if (writeMode == ReferenceComposerWriteMode.Unavailable)
                            {
                                composer.BeginInvoke(new Action(() => composer.Composer.ReadOnly = true));
                            }

                            targets.ChangeAfterApproval();
                            window.Approve();
                        }
                        else
                        {
                            window.Cancel();
                        }
                    },
                    new WindowsConfirmationOverlay.FixedForegroundNativeMethods(
                        foregroundActivated: foregroundMode == ReferenceComposerForegroundMode.Verified));

                var runtime = new NativeSubmitRuntime(
                    hookHost,
                    new NativeSubmitInterceptionController(
                        profile,
                        new NativeSubmitEmergencyState(TimeSpan.FromMinutes(1)),
                        activeSurfaceDiscovery: discovery.DiscoverActiveSurface),
                    profile,
                    ResidentTargetTracedRunner: (target, traceStage, executionGuard, executionLease) =>
                    {
                        var environment = new ReferenceComposerTransactionEnvironment(
                            sanitizer,
                            target,
                            discovery,
                            replay,
                            clipboardBoundaryFactory,
                            composerAccessMode,
                            overlay,
                            traceStage,
                            executionGuard,
                            executionLease,
                            targets.ChangeBeforeReplay);
                        var terminal = new ProtectedSendTransaction(environment).Execute(
                            new AdmittedProtectedSend(target.SnapshotGeneration, environment.TargetIdentity));
                        var result = environment.ToOsInteractionResult(terminal);
                        Volatile.Write(
                            ref transactionSnapshot,
                            new ReferenceComposerTransactionSnapshot(
                                result.Status,
                                result.Submitted,
                                environment.EvidenceLevel,
                                new Dictionary<string, string>(result.Diagnostics, StringComparer.Ordinal)));
                        return result;
                    });
                var runtimeSet = new NativeSubmitRuntimeSet(
                    hookHost,
                    new[] { runtime },
                    overlay,
                    overlay.CancelActiveConfirmation);
                var controller = new TrayProtectionController(
                    new UnavailableTrayHotkeyHost(
                        new HotkeyBinding("reference-composer", "unavailable", "acceptance"),
                        "reference_composer_manual_hotkey_disabled"),
                    () => throw new InvalidOperationException("Reference composer has no manual path."),
                    hookHost,
                    runtime.Controller,
                    profile,
                    nativeSubmitRuntimes: new[] { runtime },
                    activeSurfaceDiscovery: discovery.DiscoverActiveSurface,
                    nativeSubmitRuntimeOwner: runtimeSet);
                abort = () =>
                {
                    overlay.CancelActiveConfirmation();
                    controller.Stop();
                    if (composer.IsHandleCreated && !composer.IsDisposed)
                    {
                        composer.BeginInvoke(new Action(composer.Close));
                    }
                };
                cleanupProbe = () => !controller.State.Enabled
                    && !controller.IsNativeSubmitHookReady
                    && composer.IsDisposed
                    && replacementComposer.IsDisposed
                    && overlay.IsShutdownCompleted
                    && !hookHost.IsKeyboardHookRegistered
                    && !hookHost.IsMouseHookRegistered;

                var hookStarted = controller.Start();
                var dispatch = ReferenceOnlyInputDispatchResult.Unavailable;
                ReferenceStimulusReadinessProbe? stimulusProbe = null;
                void DispatchEnter()
                {
                    using var source = hookHost.OpenReferenceOnlyInputSourceForAcceptance(composer.Handle);
                    dispatch = source.DispatchKeyboard(new NativeKeyGesture(
                        "Enter",
                        TargetWindow: composer.Handle,
                        TargetProcessId: (uint)Environment.ProcessId));
                }

                void ActivateFixtureForEnter()
                {
                    composer.Activate();
                    composer.Composer.Focus();
                }

                ReferenceComposerFocusPrecondition ObserveFixtureFocus() =>
                    ReferenceComposerFocusPrecondition.Observe(composer, composer.Composer);

                void PublishStimulusNotReady()
                {
                    if (Interlocked.CompareExchange(ref reportFinalized, 1, 0) != 0)
                    {
                        return;
                    }

                    report = new ReferenceComposerAcceptanceReport(
                        hookStarted,
                        OriginalInputSuppressed: false,
                        TerminalStatus: OsInteractionStatusIds.FailedClosed,
                        Submitted: false,
                        SentTexts: Array.Empty<string>(),
                        Trace: Array.Empty<ProtectedSendTraceEntry>(),
                        ReplayDiagnostics: AcceptanceDiagnostics(
                            replay.Diagnostics,
                            new Dictionary<string, string>(),
                            stimulusProbe?.Diagnostics ?? new Dictionary<string, string>()),
                        CleanupPassed: false,
                        EvidenceLevel: ReferenceComposerEvidenceLevel.Unavailable);
                    completed.Set();
                    abort?.Invoke();
                }

                using var readinessProbe = new ReferenceStimulusReadinessProbe(
                    composer.Composer,
                    prompt,
                    StimulusReadinessTimeout,
                    FixtureFocusPreconditionTimeout,
                    ActivateFixtureForEnter,
                    ObserveFixtureFocus,
                    DispatchEnter,
                    PublishStimulusNotReady);
                stimulusProbe = readinessProbe;
                void FinalizeAcceptanceAtTerminal(bool deadlineReached)
                {
                    var state = controller.State;
                    var trace = state.ProtectedSendAttemptTrace;
                    var finalTransactionSnapshot = Volatile.Read(ref transactionSnapshot);
                    if (trace is not { Count: > 0 }
                        || trace[^1].Stage is not ("sent_safely" or "terminal_blocked")
                        || finalTransactionSnapshot is null)
                    {
                        return;
                    }

                    var terminalStage = trace[^1].Stage;
                    var sentCount = composer.SentCount;
                    if (!ReferenceComposerAcceptanceCompletion.IsConsistent(
                            finalTransactionSnapshot,
                            state.LastStatus,
                            state.LastSubmitted,
                            terminalStage,
                            sentCount))
                    {
                        if (!deadlineReached || Interlocked.CompareExchange(ref reportFinalized, 1, 0) != 0)
                        {
                            return;
                        }

                        var diagnostics = new Dictionary<string, string>(
                            AcceptanceDiagnostics(
                                replay.Diagnostics,
                                finalTransactionSnapshot.Diagnostics,
                                readinessProbe.Diagnostics),
                            StringComparer.Ordinal)
                        {
                            ["transaction_status"] = finalTransactionSnapshot.Status,
                            ["transaction_submitted"] = finalTransactionSnapshot.Submitted.ToString().ToLowerInvariant(),
                            ["controller_status"] = state.LastStatus,
                            ["controller_submitted"] = state.LastSubmitted.ToString().ToLowerInvariant(),
                            ["terminal_stage"] = terminalStage,
                            ["acceptance_coherent"] = "false"
                        };
                        report = new ReferenceComposerAcceptanceReport(
                            hookStarted,
                            dispatch.SuppressOriginalInput,
                            TerminalStatus: "acceptance_state_incoherent",
                            Submitted: false,
                            composer.SentTexts,
                            trace.ToArray(),
                            diagnostics,
                            CleanupPassed: false,
                            EvidenceLevel: ReferenceComposerEvidenceLevel.Unavailable);
                        completed.Set();
                        if (composer.IsHandleCreated && !composer.IsDisposed)
                        {
                            composer.BeginInvoke(new Action(composer.Close));
                        }

                        return;
                    }

                    if (Interlocked.CompareExchange(ref reportFinalized, 1, 0) != 0)
                    {
                        return;
                    }

                    // Only the production composer access path can produce
                    // acceptance evidence; the fixture never substitutes a write.
                    var evidenceLevel = ReferenceComposerAcceptanceCompletion.SelectEvidenceLevel(
                        finalTransactionSnapshot);
                    var completionDiagnostics = new Dictionary<string, string>(
                        AcceptanceDiagnostics(
                            replay.Diagnostics,
                            finalTransactionSnapshot.Diagnostics,
                            readinessProbe.Diagnostics),
                        StringComparer.Ordinal)
                    {
                        ["transaction_status"] = finalTransactionSnapshot.Status,
                        ["transaction_submitted"] = finalTransactionSnapshot.Submitted.ToString().ToLowerInvariant(),
                        ["controller_status"] = state.LastStatus,
                        ["controller_submitted"] = state.LastSubmitted.ToString().ToLowerInvariant(),
                        ["terminal_stage"] = terminalStage,
                        ["acceptance_coherent"] = "true"
                    };
                    report = new ReferenceComposerAcceptanceReport(
                        hookStarted,
                        dispatch.SuppressOriginalInput,
                        finalTransactionSnapshot.Status,
                        finalTransactionSnapshot.Submitted,
                        composer.SentTexts,
                        trace.ToArray(),
                        completionDiagnostics,
                        CleanupPassed: false,
                        EvidenceLevel: evidenceLevel);
                    completed.Set();
                    if (composer.IsHandleCreated && !composer.IsDisposed)
                    {
                        composer.BeginInvoke(new Action(composer.Close));
                    }
                }

                void StartCoherenceDeadline()
                {
                    coherenceDeadline ??= new System.Threading.Timer(_ =>
                    {
                        if (composer.IsHandleCreated && !composer.IsDisposed)
                        {
                            composer.BeginInvoke(new Action(() => FinalizeAcceptanceAtTerminal(deadlineReached: true)));
                        }
                    });
                    coherenceDeadline.Change(AcceptanceCoherenceTimeout, Timeout.InfiniteTimeSpan);
                }

                controller.StateChanged += (_, _) =>
                {
                    var state = controller.State;
                    lastAcceptanceStatus = state.LastStatus;
                    lastAcceptanceTraceStage = state.ProtectedSendAttemptTrace is { Count: > 0 } currentTrace
                        ? currentTrace[^1].Stage
                        : "none";
                    if (state.ProtectedSendAttemptTrace is not { Count: > 0 } trace
                        || trace[^1].Stage is not ("sent_safely" or "terminal_blocked"))
                    {
                        return;
                    }

                    StartCoherenceDeadline();
                    FinalizeAcceptanceAtTerminal(deadlineReached: false);
                };

                composer.Shown += (_, _) =>
                {
                    replacementComposer.CreateControl();
                    replacementComposer.Composer.CreateControl();
                    _ = replacementComposer.Handle;
                    _ = replacementComposer.Composer.Handle;
                    composer.BeginInvoke(new Action(() =>
                        readinessProbe.Start(() => composer.Composer.Text = prompt)));
                };

                try
                {
                    Application.Run(composer);
                }
                finally
                {
                    coherenceDeadline?.Dispose();
                    controller.Stop();
                }
                report ??= new ReferenceComposerAcceptanceReport(
                    hookStarted,
                    dispatch.SuppressOriginalInput,
                    OsInteractionStatusIds.FailedClosed,
                    Submitted: false,
                    composer.SentTexts.ToArray(),
                    controller.State.ProtectedSendAttemptTrace?.ToArray() ?? Array.Empty<ProtectedSendTraceEntry>(),
                    AcceptanceDiagnostics(
                        replay.Diagnostics,
                        Volatile.Read(ref transactionSnapshot)?.Diagnostics
                            ?? new Dictionary<string, string>(),
                        readinessProbe.Diagnostics),
                    CleanupPassed: false,
                    EvidenceLevel: ReferenceComposerEvidenceLevel.Unavailable);
            }
            catch (Exception exception)
            {
                failure = exception;
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "CodexRedactionGate.ReferenceComposerAcceptance"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!completed.Wait(StimulusReadinessTimeout + FixtureFocusPreconditionTimeout + TransactionCompletionTimeout))
        {
            var timeoutStatus = lastAcceptanceStatus;
            var timeoutTraceStage = lastAcceptanceTraceStage;
            abort?.Invoke();
            thread.Join(TimeSpan.FromSeconds(5));
            throw new TimeoutException(
                $"Reference composer acceptance did not reach a terminal state: status={timeoutStatus}; trace_stage={timeoutTraceStage}.");
        }

        thread.Join(TimeSpan.FromSeconds(5));
        var cleanupPassed = cleanupProbe?.Invoke() == true;
        report = report is null
            ? null
            : report with { CleanupPassed = cleanupPassed };
        if (failure is not null)
        {
            throw new InvalidOperationException("Reference composer acceptance failed.", failure);
        }

        return report ?? throw new InvalidOperationException("Reference composer acceptance did not publish a report.");
    }

    internal static ReferenceComposerAcceptanceReport Run(
        ReferenceComposerPersistentFixtureHost host,
        ISanitizer sanitizer,
        string prompt,
        ReferenceComposerDecision decision,
        ReferenceComposerForegroundMode foregroundMode = ReferenceComposerForegroundMode.Verified,
        ReferenceComposerTargetChangeMode targetChangeMode = ReferenceComposerTargetChangeMode.None,
        ReferenceComposerWriteMode writeMode = ReferenceComposerWriteMode.Available,
        ReferenceComposerReplayMode replayMode = ReferenceComposerReplayMode.Available,
        Func<IClipboardAccessBoundary>? clipboardBoundaryFactory = null,
        ReferenceComposerAccessMode composerAccessMode = ReferenceComposerAccessMode.Available)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(prompt);
        return host.RunScenario(
            composer => RunOnPersistentFixture(
                composer,
                sanitizer,
                prompt,
                decision,
                foregroundMode,
                targetChangeMode,
                writeMode,
                replayMode,
                clipboardBoundaryFactory,
                composerAccessMode,
                host.RecordScenarioComposition,
                host.ReacquireLeaseFocus));
    }

    private static ReferenceComposerAcceptanceReport RunOnPersistentFixture(
        ReferenceComposerForm composer,
        ISanitizer sanitizer,
        string prompt,
        ReferenceComposerDecision decision,
        ReferenceComposerForegroundMode foregroundMode,
        ReferenceComposerTargetChangeMode targetChangeMode,
        ReferenceComposerWriteMode writeMode,
        ReferenceComposerReplayMode replayMode,
        Func<IClipboardAccessBoundary>? clipboardBoundaryFactory,
        ReferenceComposerAccessMode composerAccessMode,
        Action<object, object> recordComposition,
        Action<ReferenceComposerForm> reacquireLeaseFocus)
    {
        ReferenceComposerAcceptanceReport? report = null;
        ReferenceComposerTransactionSnapshot? transactionSnapshot = null;
        using var completed = new ManualResetEventSlim();
        ReferenceStimulusReadinessProbe? readinessProbe = null;
        System.Threading.Timer? coherenceDeadline = null;
        ReferenceComposerForm? replacementComposer = null;
        WindowsConfirmationOverlay? overlay = null;
        WindowsNativeSubmitHookHost? hookHost = null;
        TrayProtectionController? controller = null;
        var dispatch = ReferenceOnlyInputDispatchResult.Unavailable;
        var reportFinalized = 0;
        var lastAcceptanceStatus = "not_started";
        var lastAcceptanceTraceStage = "none";
        var hookStarted = false;
        Exception? failure = null;

        try
        {
            if (targetChangeMode != ReferenceComposerTargetChangeMode.None)
            {
                replacementComposer = new ReferenceComposerForm();
                replacementComposer.CreateControl();
                replacementComposer.Composer.CreateControl();
                _ = replacementComposer.Handle;
                _ = replacementComposer.Composer.Handle;
            }

            var targets = new ReferenceComposerTargetController(composer, replacementComposer, targetChangeMode);
            var discovery = new ReferenceComposerSurfaceDiscovery(targets.GetActiveForm);
            var profile = CreateProfile();
            var replay = new ReferenceComposerReplayBoundary(composer, replayMode);
            hookHost = new WindowsNativeSubmitHookHost(new[] { profile });
            overlay = new WindowsConfirmationOverlay(
                window =>
                {
                    if (decision == ReferenceComposerDecision.Approve)
                    {
                        if (writeMode == ReferenceComposerWriteMode.Unavailable)
                        {
                            composer.BeginInvoke(new Action(() => composer.Composer.ReadOnly = true));
                        }

                        targets.ChangeAfterApproval();
                        window.Approve();
                    }
                    else
                    {
                        window.Cancel();
                    }
                },
                new WindowsConfirmationOverlay.FixedForegroundNativeMethods(
                    foregroundActivated: foregroundMode == ReferenceComposerForegroundMode.Verified));

            var runtime = new NativeSubmitRuntime(
                hookHost,
                new NativeSubmitInterceptionController(
                    profile,
                    new NativeSubmitEmergencyState(TimeSpan.FromMinutes(1)),
                    activeSurfaceDiscovery: discovery.DiscoverActiveSurface),
                profile,
                ResidentTargetTracedRunner: (target, traceStage, executionGuard, executionLease) =>
                {
                    var environment = new ReferenceComposerTransactionEnvironment(
                        sanitizer,
                        target,
                        discovery,
                        replay,
                        clipboardBoundaryFactory,
                        composerAccessMode,
                        overlay,
                        traceStage,
                        executionGuard,
                        executionLease,
                        targets.ChangeBeforeReplay);
                    var terminal = new ProtectedSendTransaction(environment).Execute(
                        new AdmittedProtectedSend(target.SnapshotGeneration, environment.TargetIdentity));
                    var result = environment.ToOsInteractionResult(terminal);
                    Volatile.Write(
                        ref transactionSnapshot,
                        new ReferenceComposerTransactionSnapshot(
                            result.Status,
                            result.Submitted,
                            environment.EvidenceLevel,
                            new Dictionary<string, string>(result.Diagnostics, StringComparer.Ordinal)));
                    return result;
                });
            var runtimeSet = new NativeSubmitRuntimeSet(
                hookHost,
                new[] { runtime },
                overlay,
                overlay.CancelActiveConfirmation);
            controller = new TrayProtectionController(
                new UnavailableTrayHotkeyHost(
                    new HotkeyBinding("reference-composer", "unavailable", "acceptance"),
                    "reference_composer_manual_hotkey_disabled"),
                () => throw new InvalidOperationException("Reference composer has no manual path."),
                hookHost,
                runtime.Controller,
                profile,
                nativeSubmitRuntimes: new[] { runtime },
                activeSurfaceDiscovery: discovery.DiscoverActiveSurface,
                nativeSubmitRuntimeOwner: runtimeSet);
            recordComposition(controller, runtime);
            hookStarted = controller.Start();

            void DispatchEnter()
            {
                using var source = hookHost.OpenReferenceOnlyInputSourceForAcceptance(composer.Handle);
                dispatch = source.DispatchKeyboard(new NativeKeyGesture(
                    "Enter",
                    TargetWindow: composer.Handle,
                    TargetProcessId: (uint)Environment.ProcessId));
            }

            void PublishStimulusNotReady()
            {
                if (Interlocked.CompareExchange(ref reportFinalized, 1, 0) != 0)
                {
                    return;
                }

                report = new ReferenceComposerAcceptanceReport(
                    hookStarted,
                    OriginalInputSuppressed: false,
                    TerminalStatus: OsInteractionStatusIds.FailedClosed,
                    Submitted: false,
                    SentTexts: Array.Empty<string>(),
                    Trace: Array.Empty<ProtectedSendTraceEntry>(),
                    ReplayDiagnostics: AcceptanceDiagnostics(
                        replay.Diagnostics,
                        new Dictionary<string, string>(),
                        readinessProbe?.Diagnostics ?? new Dictionary<string, string>()),
                    CleanupPassed: false,
                    EvidenceLevel: ReferenceComposerEvidenceLevel.Unavailable);
                completed.Set();
            }

            readinessProbe = new ReferenceStimulusReadinessProbe(
                composer.Composer,
                prompt,
                StimulusReadinessTimeout,
                FixtureFocusPreconditionTimeout,
                activateFixture: () => reacquireLeaseFocus(composer),
                () => ReferenceComposerFocusPrecondition.Observe(composer, composer.Composer),
                DispatchEnter,
                PublishStimulusNotReady);

            void FinalizeAcceptanceAtTerminal(bool deadlineReached)
            {
                var state = controller.State;
                var trace = state.ProtectedSendAttemptTrace;
                var finalTransactionSnapshot = Volatile.Read(ref transactionSnapshot);
                if (trace is not { Count: > 0 }
                    || trace[^1].Stage is not ("sent_safely" or "terminal_blocked")
                    || finalTransactionSnapshot is null)
                {
                    return;
                }

                var terminalStage = trace[^1].Stage;
                var sentCount = composer.SentCount;
                if (!ReferenceComposerAcceptanceCompletion.IsConsistent(
                        finalTransactionSnapshot,
                        state.LastStatus,
                        state.LastSubmitted,
                        terminalStage,
                        sentCount))
                {
                    if (!deadlineReached || Interlocked.CompareExchange(ref reportFinalized, 1, 0) != 0)
                    {
                        return;
                    }

                    var diagnostics = new Dictionary<string, string>(
                        AcceptanceDiagnostics(
                            replay.Diagnostics,
                            finalTransactionSnapshot.Diagnostics,
                            readinessProbe.Diagnostics),
                        StringComparer.Ordinal)
                    {
                        ["transaction_status"] = finalTransactionSnapshot.Status,
                        ["transaction_submitted"] = finalTransactionSnapshot.Submitted.ToString().ToLowerInvariant(),
                        ["controller_status"] = state.LastStatus,
                        ["controller_submitted"] = state.LastSubmitted.ToString().ToLowerInvariant(),
                        ["terminal_stage"] = terminalStage,
                        ["acceptance_coherent"] = "false"
                    };
                    report = new ReferenceComposerAcceptanceReport(
                        hookStarted,
                        dispatch.SuppressOriginalInput,
                        TerminalStatus: "acceptance_state_incoherent",
                        Submitted: false,
                        composer.SentTexts,
                        trace.ToArray(),
                        diagnostics,
                        CleanupPassed: false,
                        EvidenceLevel: ReferenceComposerEvidenceLevel.Unavailable);
                    completed.Set();
                    return;
                }

                if (Interlocked.CompareExchange(ref reportFinalized, 1, 0) != 0)
                {
                    return;
                }

                var evidenceLevel = ReferenceComposerAcceptanceCompletion.SelectEvidenceLevel(
                    finalTransactionSnapshot);
                var completionDiagnostics = new Dictionary<string, string>(
                    AcceptanceDiagnostics(
                        replay.Diagnostics,
                        finalTransactionSnapshot.Diagnostics,
                        readinessProbe.Diagnostics),
                    StringComparer.Ordinal)
                {
                    ["transaction_status"] = finalTransactionSnapshot.Status,
                    ["transaction_submitted"] = finalTransactionSnapshot.Submitted.ToString().ToLowerInvariant(),
                    ["controller_status"] = state.LastStatus,
                    ["controller_submitted"] = state.LastSubmitted.ToString().ToLowerInvariant(),
                    ["terminal_stage"] = terminalStage,
                    ["acceptance_coherent"] = "true"
                };
                report = new ReferenceComposerAcceptanceReport(
                    hookStarted,
                    dispatch.SuppressOriginalInput,
                    finalTransactionSnapshot.Status,
                    finalTransactionSnapshot.Submitted,
                    composer.SentTexts,
                    trace.ToArray(),
                    completionDiagnostics,
                    CleanupPassed: false,
                    EvidenceLevel: evidenceLevel);
                completed.Set();
            }

            void StartCoherenceDeadline()
            {
                coherenceDeadline ??= new System.Threading.Timer(_ =>
                {
                    if (composer.IsHandleCreated && !composer.IsDisposed)
                    {
                        composer.BeginInvoke(new Action(() => FinalizeAcceptanceAtTerminal(deadlineReached: true)));
                    }
                });
                coherenceDeadline.Change(AcceptanceCoherenceTimeout, Timeout.InfiniteTimeSpan);
            }

            controller.StateChanged += (_, _) =>
            {
                var state = controller.State;
                lastAcceptanceStatus = state.LastStatus;
                lastAcceptanceTraceStage = state.ProtectedSendAttemptTrace is { Count: > 0 } currentTrace
                    ? currentTrace[^1].Stage
                    : "none";
                if (state.ProtectedSendAttemptTrace is not { Count: > 0 } trace
                    || trace[^1].Stage is not ("sent_safely" or "terminal_blocked"))
                {
                    return;
                }

                StartCoherenceDeadline();
                FinalizeAcceptanceAtTerminal(deadlineReached: false);
            };

            composer.BeginInvoke(new Action(() => readinessProbe.Start(() => composer.Composer.Text = prompt)));
            var deadline = Environment.TickCount64
                + (long)(StimulusReadinessTimeout + FixtureFocusPreconditionTimeout + TransactionCompletionTimeout).TotalMilliseconds;
            while (!completed.IsSet && Environment.TickCount64 < deadline)
            {
                Application.DoEvents();
                completed.Wait(10);
            }

            if (!completed.IsSet)
            {
                throw new TimeoutException(
                    $"Persistent reference scenario did not reach terminal state: status={lastAcceptanceStatus}; trace_stage={lastAcceptanceTraceStage}.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            coherenceDeadline?.Dispose();
            readinessProbe?.Dispose();
            try
            {
                overlay?.CancelActiveConfirmation();
                controller?.Stop();
            }
            finally
            {
                overlay?.Dispose();
                replacementComposer?.Dispose();
            }
        }

        var cleanupPassed = controller is not null
            && !controller.State.Enabled
            && !controller.IsNativeSubmitHookReady
            && (replacementComposer is null || replacementComposer.IsDisposed)
            && overlay?.IsShutdownCompleted == true
            && hookHost is not null
            && !hookHost.IsKeyboardHookRegistered
            && !hookHost.IsMouseHookRegistered;
        report = report is null ? null : report with { CleanupPassed = cleanupPassed };
        if (failure is not null)
        {
            throw new InvalidOperationException("Persistent reference composer acceptance failed.", failure);
        }

        return report ?? throw new InvalidOperationException("Persistent reference composer acceptance did not publish a report.");
    }

    internal sealed class ReferenceComposerPersistentFixtureHost : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _focusCompleted = new();
        private readonly object _leaseGate = new();
        private ReferenceComposerForm? _composer;
        private ReferenceComposerPersistentFixtureLifecycle? _lifecycle;
        private bool _initialFocusReady;
        private ReferenceComposerFixtureFocusRequestResult _initialFocusRequest;
        private bool _disposed;
        private bool _formDisposed;
        private readonly List<int> _controllerIdentities = new();
        private readonly List<int> _runtimeIdentities = new();

        internal ReferenceComposerPersistentFixtureHost()
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException();
            }

            _thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "CodexRedactionGate.ReferenceComposerPersistentFixture"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _focusCompleted.Wait(FixtureFocusPreconditionTimeout + TimeSpan.FromSeconds(5));
        }

        internal bool InitialFocusReady => _initialFocusReady;
        internal bool InitialSetForegroundRequestSucceeded => _initialFocusRequest.SetForegroundRequestSucceeded;
        internal bool InitialFocusRequestSucceeded => _initialFocusRequest.FocusRequestSucceeded;
        internal int UiThreadId => _lifecycle?.UiThreadId ?? 0;
        internal long FormIdentity => _lifecycle?.FormIdentity ?? 0;
        internal long ControlIdentity => _lifecycle?.ControlIdentity ?? 0;
        internal int InitialFocusHandoffCount => _lifecycle?.InitialFocusHandoffCount ?? 0;
        internal int LeaseCount => _lifecycle?.LeaseCount ?? 0;
        internal int LeaseFocusReacquisitionCount => _lifecycle?.LeaseFocusReacquisitionCount ?? 0;
        internal int CompletedLeaseCount => _lifecycle?.CompletedLeaseCount ?? 0;
        internal bool AllLeaseCleanupPassed => _lifecycle?.AllLeaseCleanupPassed == true;
        internal bool FinalCleanupPassed => _lifecycle?.FinalCleanupPassed == true;
        internal bool ThreadStopped => !_thread.IsAlive;
        internal bool FormDisposed => _formDisposed;
        internal IReadOnlyList<int> ControllerIdentities => _controllerIdentities.ToArray();
        internal IReadOnlyList<int> RuntimeIdentities => _runtimeIdentities.ToArray();

        internal ReferenceComposerAcceptanceReport RunScenario(
            Func<ReferenceComposerForm, ReferenceComposerAcceptanceReport> run)
        {
            ArgumentNullException.ThrowIfNull(run);
            lock (_leaseGate)
            {
                var composer = _composer;
                var lifecycle = _lifecycle;
                if (_disposed
                    || !_initialFocusReady
                    || composer is null
                    || lifecycle is null
                    || composer.IsDisposed)
                {
                    throw new InvalidOperationException("Persistent reference fixture lease is unavailable.");
                }

                return (ReferenceComposerAcceptanceReport)composer.Invoke(
                    new Func<ReferenceComposerAcceptanceReport>(() =>
                    {
                        if (!lifecycle.TryBeginLease(
                                Environment.CurrentManagedThreadId,
                                composer.Handle.ToInt64(),
                                composer.Composer.Handle.ToInt64()))
                        {
                            throw new InvalidOperationException("Persistent reference fixture identity changed.");
                        }

                        try
                        {
                            composer.ResetScenarioState();
                            var report = run(composer);
                            lifecycle.RecordScenarioState(
                                composer.SentCount,
                                report.Trace.LastOrDefault()?.Stage ?? "none");
                            lifecycle.CompleteLease(report.CleanupPassed);
                            return report;
                        }
                        catch
                        {
                            lifecycle.CompleteLease(cleanupPassed: false);
                            throw;
                        }
                    }));
            }
        }

        internal void RecordScenarioComposition(object controller, object runtime)
        {
            _controllerIdentities.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(controller));
            _runtimeIdentities.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(runtime));
        }

        internal void ReacquireLeaseFocus(ReferenceComposerForm composer)
        {
            ArgumentNullException.ThrowIfNull(composer);
            var lifecycle = _lifecycle;
            if (lifecycle is null
                || composer.InvokeRequired
                || !lifecycle.TryRecordLeaseFocusReacquisition(
                    Environment.CurrentManagedThreadId,
                    composer.Handle.ToInt64(),
                    composer.Composer.Handle.ToInt64()))
            {
                throw new InvalidOperationException("Persistent reference fixture focus reacquisition is unavailable.");
            }

            RequestFixtureForeground(composer);
        }

        private void RunMessageLoop()
        {
            System.Windows.Forms.Timer? focusTimer = null;
            try
            {
                _ = Application.OleRequired();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var composer = new ReferenceComposerForm();
                _composer = composer;
                composer.Shown += (_, _) =>
                {
                    _ = composer.Handle;
                    _ = composer.Composer.Handle;
                    _lifecycle = new ReferenceComposerPersistentFixtureLifecycle(
                        Environment.CurrentManagedThreadId,
                        composer.Handle.ToInt64(),
                        composer.Composer.Handle.ToInt64());
                    var deadline = Environment.TickCount64 + (long)FixtureFocusPreconditionTimeout.TotalMilliseconds;
                    _initialFocusRequest = RequestFixtureForeground(composer);
                    focusTimer = new System.Windows.Forms.Timer { Interval = 100 };
                    focusTimer.Tick += (_, _) =>
                    {
                        var observed = ReferenceComposerFocusPrecondition.Observe(composer, composer.Composer);
                        if (!observed.Passed && Environment.TickCount64 < deadline)
                        {
                            return;
                        }

                        focusTimer.Stop();
                        _initialFocusReady = observed.Passed;
                        _lifecycle.RecordInitialFocus(
                            observed.ForegroundMatchesTarget,
                            observed.FocusedElementMatchesTarget);
                        _focusCompleted.Set();
                    };
                    focusTimer.Start();
                };
                Application.Run(composer);
                _formDisposed = composer.IsDisposed;
            }
            finally
            {
                focusTimer?.Dispose();
                _focusCompleted.Set();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var composer = _composer;
            if (composer is not null && composer.IsHandleCreated && !composer.IsDisposed)
            {
                composer.BeginInvoke(new Action(composer.Close));
            }

            _thread.Join(TimeSpan.FromSeconds(5));
            _lifecycle?.Dispose(_formDisposed && !_thread.IsAlive);
            _focusCompleted.Dispose();
        }

        private static ReferenceComposerFixtureFocusRequestResult RequestFixtureForeground(
            ReferenceComposerForm composer)
        {
            ArgumentNullException.ThrowIfNull(composer);
            if (composer.InvokeRequired)
            {
                throw new InvalidOperationException("Reference fixture foreground request must run on its owner STA.");
            }

            // The initial Shown callback and every persistent lease keep this
            // form visible. Requesting Show again would add a second lifecycle
            // owner; activation remains best-effort and is never evidence.
            composer.Activate();
            var setForegroundRequestSucceeded = NativeMethods.SetForegroundWindow(composer.Handle);
            var focusRequestSucceeded = composer.Composer.Focus();
            return new ReferenceComposerFixtureFocusRequestResult(
                setForegroundRequestSucceeded,
                focusRequestSucceeded);
        }

        private readonly record struct ReferenceComposerFixtureFocusRequestResult(
            bool SetForegroundRequestSucceeded,
            bool FocusRequestSucceeded);

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern bool SetForegroundWindow(IntPtr hWnd);
        }
    }

    private sealed class ReferenceStimulusReadinessProbe : IDisposable
    {
        private readonly object _gate = new();
        private readonly TextBox _composer;
        private readonly string _expectedPrompt;
        private readonly Action _activateFixture;
        private readonly Func<ReferenceComposerFocusPrecondition> _observeFixtureFocus;
        private readonly Action _dispatchEnter;
        private readonly Action _onTimeout;
        private readonly System.Threading.Timer _deadlineTimer;
        private readonly System.Windows.Forms.Timer _focusTimer;
        private readonly List<string> _transitions = new() { "not_started" };
        private readonly TimeSpan _timeout;
        private readonly TimeSpan _focusTimeout;
        private string _state = "not_started";
        private bool _timedOut;
        private string _terminalReason = "none";
        private string _fixtureForegroundReady = "unavailable";
        private string _fixtureFocusedElementReady = "unavailable";
        private long _focusDeadline;
        private bool _disposed;

        internal ReferenceStimulusReadinessProbe(
            TextBox composer,
            string expectedPrompt,
            TimeSpan timeout,
            TimeSpan focusTimeout,
            Action activateFixture,
            Func<ReferenceComposerFocusPrecondition> observeFixtureFocus,
            Action dispatchEnter,
            Action onTimeout)
        {
            _composer = composer ?? throw new ArgumentNullException(nameof(composer));
            _expectedPrompt = expectedPrompt ?? throw new ArgumentNullException(nameof(expectedPrompt));
            _timeout = timeout > TimeSpan.Zero ? timeout : throw new ArgumentOutOfRangeException(nameof(timeout));
            _focusTimeout = focusTimeout > TimeSpan.Zero ? focusTimeout : throw new ArgumentOutOfRangeException(nameof(focusTimeout));
            _activateFixture = activateFixture ?? throw new ArgumentNullException(nameof(activateFixture));
            _observeFixtureFocus = observeFixtureFocus ?? throw new ArgumentNullException(nameof(observeFixtureFocus));
            _dispatchEnter = dispatchEnter ?? throw new ArgumentNullException(nameof(dispatchEnter));
            _onTimeout = onTimeout ?? throw new ArgumentNullException(nameof(onTimeout));
            _deadlineTimer = new System.Threading.Timer(OnDeadline, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _focusTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _focusTimer.Tick += OnFocusTimerTick;
            _composer.TextChanged += OnTextChanged;
        }

        internal IReadOnlyDictionary<string, string> Diagnostics
        {
            get
            {
                lock (_gate)
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["stimulus_readiness_state"] = _state,
                        ["stimulus_transition_sequence"] = string.Join(",", _transitions),
                        ["stimulus_timeout"] = _timedOut.ToString().ToLowerInvariant(),
                        ["stimulus_terminal_reason"] = _terminalReason,
                        ["fixture_foreground_ready"] = _fixtureForegroundReady,
                        ["fixture_focused_element_ready"] = _fixtureFocusedElementReady
                    };
                }
            }
        }

        internal void Start(Action seedPrompt)
        {
            ArgumentNullException.ThrowIfNull(seedPrompt);
            lock (_gate)
            {
                if (_disposed || _state != "not_started")
                {
                    return;
                }

                Transition("typing");
                _deadlineTimer.Change(_timeout, Timeout.InfiniteTimeSpan);
            }

            seedPrompt();
            ObserveInputReadiness();
        }

        private void OnTextChanged(object? sender, EventArgs eventArgs) => ObserveInputReadiness();

        private void ObserveInputReadiness()
        {
            var queueEnter = false;
            lock (_gate)
            {
                if (_disposed
                    || _state != "typing"
                    || _composer.TextLength == 0
                    || !string.Equals(_composer.Text, _expectedPrompt, StringComparison.Ordinal))
                {
                    return;
                }

                Transition("input_ready");
                Transition("enter_queued");
                _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                queueEnter = true;
            }

            if (queueEnter)
            {
                _composer.BeginInvoke(new Action(DispatchEnter));
            }
        }

        private void DispatchEnter()
        {
            lock (_gate)
            {
                if (_disposed || _state != "enter_queued")
                {
                    return;
                }
            }

            _activateFixture();
            _focusDeadline = Environment.TickCount64 + (long)_focusTimeout.TotalMilliseconds;
            ObserveFocusPrecondition();
        }

        private void OnFocusTimerTick(object? sender, EventArgs eventArgs) => ObserveFocusPrecondition();

        private void ObserveFocusPrecondition()
        {
            var expired = Environment.TickCount64 >= _focusDeadline;
            var precondition = expired
                ? new ReferenceComposerFocusPrecondition(false, false)
                : _observeFixtureFocus();
            var dispatchEnter = false;
            var preconditionFailed = false;
            lock (_gate)
            {
                if (_disposed || _state != "enter_queued")
                {
                    return;
                }

                _fixtureForegroundReady = precondition.ForegroundMatchesTarget.ToString().ToLowerInvariant();
                _fixtureFocusedElementReady = precondition.FocusedElementMatchesTarget.ToString().ToLowerInvariant();
                if (precondition.Passed)
                {
                    _focusTimer.Stop();
                    Transition("enter_dispatched");
                    dispatchEnter = true;
                }
                else if (expired)
                {
                    _focusTimer.Stop();
                    _terminalReason = "fixture_focus_precondition_unavailable";
                    Transition("fixture_focus_precondition_unavailable");
                    preconditionFailed = true;
                }
                else
                {
                    _focusTimer.Start();
                }
            }

            if (preconditionFailed)
            {
                _onTimeout();
                return;
            }

            if (dispatchEnter)
            {
                _dispatchEnter();
            }
        }

        private void OnDeadline(object? state)
        {
            lock (_gate)
            {
                if (_disposed || _state != "typing")
                {
                    return;
                }

                _timedOut = true;
                _terminalReason = "stimulus_not_ready";
                Transition("stimulus_not_ready");
            }

            _onTimeout();
        }

        private void Transition(string state)
        {
            _state = state;
            _transitions.Add(state);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _deadlineTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _focusTimer.Stop();
            }

            _composer.TextChanged -= OnTextChanged;
            _focusTimer.Tick -= OnFocusTimerTick;
            _focusTimer.Dispose();
            _deadlineTimer.Dispose();
        }
    }

    private readonly record struct ReferenceComposerFocusPrecondition(
        bool ForegroundMatchesTarget,
        bool FocusedElementMatchesTarget)
    {
        internal bool Passed => ForegroundMatchesTarget && FocusedElementMatchesTarget;

        internal static ReferenceComposerFocusPrecondition Observe(Form form, Control composer)
        {
            try
            {
                var foregroundMatches = NativeMethods.GetForegroundWindow() == form.Handle;
                var target = AutomationElement.FromHandle(composer.Handle);
                var focused = AutomationElement.FocusedElement;
                var focusedMatches = target is not null
                    && focused is not null
                    && Automation.Compare(focused, target);
                return new ReferenceComposerFocusPrecondition(foregroundMatches, focusedMatches);
            }
            catch
            {
                return new ReferenceComposerFocusPrecondition(false, false);
            }
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern IntPtr GetForegroundWindow();
        }
    }

    private static SubmitBindingProfile CreateProfile()
    {
        var newline = SubmitKeyBinding.Parse("Ctrl+Enter").Binding!;
        return new SubmitBindingProfile(
            ReferenceOnlyInputSource.ProfileId,
            Enabled: true,
            BindingSource: "product_verified",
            SubmitBinding: ReferenceOnlyInputSource.SubmitBinding,
            NewlineBinding: newline,
            CapabilityStatus: OsInteractionStatusIds.Protected,
            CompatibilityEvidence: null,
            Diagnostics: new Dictionary<string, string>());
    }

    private sealed class ReferenceComposerSurfaceDiscovery : IActiveTextSurfaceDiscovery
    {
        private readonly Func<ReferenceComposerForm> _formProvider;

        public ReferenceComposerSurfaceDiscovery(Func<ReferenceComposerForm> formProvider)
        {
            _formProvider = formProvider;
        }

        public TextSurfaceDiscoveryResult DiscoverActiveSurface()
        {
            var form = _formProvider();
            if (form.IsDisposed || !form.IsHandleCreated || !form.Composer.IsHandleCreated)
            {
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }

            try
            {
                var element = AutomationElement.FromHandle(form.Composer.Handle);
                if (element is null || element.Current.ProcessId != Environment.ProcessId)
                {
                    return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
                }

                var automationId = element.Current.AutomationId;
                if (string.IsNullOrWhiteSpace(automationId))
                {
                    return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
                }

                var metadata = new SurfaceMetadata(
                    SurfaceKind: "reference_only_acceptance",
                    CloudSubmission: "false",
                    ComposerStatus: OsInteractionStatusIds.SupportedComposer,
                    WindowHandle: form.Handle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture),
                    ElementAutomationId: automationId,
                    ArbitraryMetadata: new Dictionary<string, string>
                    {
                        ["focused_element_hash"] = "reference-composer-uia",
                        ["submit_binding"] = ReferenceOnlyInputSource.SubmitBinding.DisplayText,
                        ["submit_binding_sendkeys"] = ReferenceOnlyInputSource.SubmitBinding.SendKeysText,
                        ["keyboard_write_fallback"] = "true"
                    });
                return TextSurfaceDiscoveryResult.Success(new TextSurfaceDescriptor(
                    "reference-composer",
                    ReferenceOnlyInputSource.ProfileId,
                    "Reference composer",
                    Supported: true,
                    CanCaptureText: true,
                    CanReplaceText: true,
                    CanSubmit: true,
                    metadata));
            }
            catch (ElementNotAvailableException)
            {
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }
            catch (InvalidOperationException)
            {
                return TextSurfaceDiscoveryResult.Failure(OsInteractionStatusIds.NotComposer);
            }
        }

        TextSurfaceDiscoveryResult IActiveTextSurfaceDiscovery.DiscoverActiveSurface()
            => DiscoverActiveSurface();
    }

    /// <summary>
    /// Minimal reference composer fixture. Arrange-only seeding writes the prompt
    /// to the TextBox before native Enter dispatch. It exposes only the resulting
    /// raw-free submit observation and never supplies a read, write, or replay
    /// fallback; all transaction side effects go through
    /// <see cref="NativeVerifiedComposerTextAccess"/>.
    /// </summary>
    internal sealed class ReferenceComposerForm : Form
    {
        private readonly List<string> _sentTexts = new();
        private int _sentCount;

        public ReferenceComposerForm()
        {
            Text = "Code Sanitizer reference composer";
            Width = 640;
            Height = 260;
            ShowInTaskbar = true;
            TopMost = true;
            KeyPreview = true;
            Composer = new TextBox
            {
                Name = "ReferenceComposerInput",
                AccessibleName = "ReferenceComposerInput",
                Multiline = true,
                Dock = DockStyle.Fill
            };
            Controls.Add(Composer);
            KeyDown += OnKeyDown;
        }

        public TextBox Composer { get; }

        public IReadOnlyList<string> SentTexts
        {
            get
            {
                lock (_sentTexts)
                {
                    return _sentTexts.ToArray();
                }
            }
        }

        public int SentCount => Volatile.Read(ref _sentCount);

        public void ResetScenarioState()
        {
            if (InvokeRequired)
            {
                throw new InvalidOperationException("Reference fixture reset must run on its UI thread.");
            }

            Composer.ReadOnly = false;
            Composer.Text = string.Empty;
            lock (_sentTexts)
            {
                _sentTexts.Clear();
            }

            Volatile.Write(ref _sentCount, 0);
        }

        public bool WaitForExactlyOneAdditionalSubmit(int previousCount, TimeSpan timeout)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                var currentCount = SentCount;
                if (currentCount != previousCount)
                {
                    return currentCount == previousCount + 1;
                }

                Thread.Sleep(10);
            }

            return SentCount == previousCount + 1;
        }

        private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.KeyCode != Keys.Enter || eventArgs.Control || eventArgs.Alt || eventArgs.Shift)
            {
                return;
            }

            eventArgs.SuppressKeyPress = true;
            lock (_sentTexts)
            {
                _sentTexts.Add(Composer.Text);
            }
            Interlocked.Increment(ref _sentCount);
        }
    }

    internal sealed class ReferenceComposerTransactionEnvironment : IProtectedSendTransactionEnvironment
    {
        private readonly ISanitizer _sanitizer;
        private readonly NativeSubmitTargetIdentity _capturedTarget;
        private readonly IActiveTextSurfaceDiscovery _surfaceDiscovery;
        private readonly IVerifiedComposerReplay _replay;
        private readonly Func<IClipboardAccessBoundary>? _clipboardBoundaryFactory;
        private readonly ReferenceComposerAccessMode _accessMode;
        private readonly IConfirmationOverlay _overlay;
        private readonly Func<string, string, bool> _traceStage;
        private readonly Func<bool> _executionGuard;
        private readonly Func<IDisposable?> _executionLease;
        private readonly Action _afterVerified;
        private readonly Func<IProtectedSendTransactionSession>? _sessionFactory;
        private readonly Dictionary<string, string> _diagnostics = new(StringComparer.Ordinal);
        private readonly object _terminalGate = new();
        private ProtectedSendTerminalEvidence? _terminalEvidence;

        internal ReferenceComposerTransactionEnvironment(
            ISanitizer sanitizer,
            NativeSubmitTargetIdentity capturedTarget,
            IActiveTextSurfaceDiscovery surfaceDiscovery,
            IVerifiedComposerReplay replay,
            Func<IClipboardAccessBoundary>? clipboardBoundaryFactory,
            ReferenceComposerAccessMode accessMode,
            IConfirmationOverlay overlay,
            Func<string, string, bool> traceStage,
            Func<bool> executionGuard,
            Func<IDisposable?> executionLease,
            Action afterVerified,
            Func<IProtectedSendTransactionSession>? sessionFactory = null)
        {
            _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
            _capturedTarget = capturedTarget ?? throw new ArgumentNullException(nameof(capturedTarget));
            _surfaceDiscovery = surfaceDiscovery ?? throw new ArgumentNullException(nameof(surfaceDiscovery));
            _replay = replay ?? throw new ArgumentNullException(nameof(replay));
            _clipboardBoundaryFactory = clipboardBoundaryFactory;
            _accessMode = accessMode;
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _traceStage = traceStage ?? throw new ArgumentNullException(nameof(traceStage));
            _executionGuard = executionGuard ?? throw new ArgumentNullException(nameof(executionGuard));
            _executionLease = executionLease ?? throw new ArgumentNullException(nameof(executionLease));
            _afterVerified = afterVerified ?? throw new ArgumentNullException(nameof(afterVerified));
            _sessionFactory = sessionFactory;
            _diagnostics["composer_read_outcome"] = "not_completed";
        }

        internal string TargetIdentity => $"{_capturedTarget.ProfileId}:{_capturedTarget.WindowHandle}";
        internal IReadOnlyDictionary<string, string> Diagnostics => _diagnostics;
        internal ReferenceComposerEvidenceLevel EvidenceLevel => _nativeVerifiedComposerAccessObserved
            ? ReferenceComposerEvidenceLevel.ReferenceProductionAccess
            : ReferenceComposerEvidenceLevel.Unavailable;
        private string TerminalStatus { get; set; } = OsInteractionStatusIds.FailedClosed;
        private bool Applied { get; set; }
        private bool _nativeVerifiedComposerAccessObserved;

        public IProtectedSendTransactionSession CreateSession(AdmittedProtectedSend request)
        {
            if (_sessionFactory is not null)
            {
                return _sessionFactory();
            }

            var capturedDiscovery = new CapturedTargetSurfaceDiscovery(_surfaceDiscovery, _capturedTarget);
            IVerifiedComposerTextAccess textAccess = _accessMode == ReferenceComposerAccessMode.Available
                ? new NativeVerifiedComposerTextAccess(
                    capturedDiscovery.DiscoverActiveSurface,
                    _replay,
                    new NativeStaExecutionBoundary(),
                    clipboard: _clipboardBoundaryFactory?.Invoke())
                : new UnsupportedVerifiedComposerTextAccess();
            var adapter = new WindowsVerifiedComposerSurfaceAdapter(textAccess);
            var session = new WindowsProtectedComposerSessionFactory(capturedDiscovery, adapter).Create();
            return new ReferenceComposerTransactionSession(session, TargetIdentity, this);
        }

        public SanitizedPrompt Sanitize(string prompt)
        {
            var result = _sanitizer.Sanitize(new SanitizeRequest(
                new[] { new ContentPart("prompt", ContentSources.PromptText, prompt, new Dictionary<string, string>()) },
                new SanitizationContext(ReferenceOnlyInputSource.ProfileId, null, null, null, "default"),
                new SanitizationOptions(false, false, "reference-transaction")));
            _diagnostics["decision"] = result.Decision.ToString().ToLowerInvariant();
            return new SanitizedPrompt(result.SanitizedText, result.Decision == SanitizeDecision.Confirm);
        }

        public SanitizedPromptDecision Confirm(SanitizedPrompt prompt)
        {
            var model = new ConfirmationUiModel(prompt.Text, Array.Empty<HighlightedReplacementSpan>(),
                new Dictionary<string, int>(), Array.Empty<string>(), "Confirm sanitized prompt", "Cancel", false);
            var decision = _overlay is ITracedConfirmationOverlay traced
                ? traced.RequestConfirmation(model, _traceStage)
                : _overlay.RequestConfirmation(model);
            if (!decision.Approved || decision.Payload is null)
            {
                return _traceStage("cancelled", "user_cancelled")
                    ? SanitizedPromptDecision.Cancel()
                    : new SanitizedPromptDecision(SanitizedPromptDecisionKind.Edit, null);
            }

            return _traceStage("approved", "user_approved")
                ? SanitizedPromptDecision.Confirm()
                : new SanitizedPromptDecision(SanitizedPromptDecisionKind.Edit, null);
        }

        public bool IsGenerationCurrent(AdmittedProtectedSend request) => request.Generation == _capturedTarget.SnapshotGeneration;
        public bool CanContinue(AdmittedProtectedSend request) => IsGenerationCurrent(request) && _executionGuard();
        public IDisposable? AcquireLease(AdmittedProtectedSend request) => CanContinue(request) ? _executionLease() : null;

        public bool PublishTrace(ProtectedSendTraceEvidence trace)
        {
            var mapped = trace.Stage switch
            {
                ProtectedSendTransactionStage.Read => ("composer_read", "capture_verified"),
                ProtectedSendTransactionStage.Sanitized => ("sanitized", "sanitization_verified"),
                ProtectedSendTransactionStage.Confirmation => ("overlay_created", "confirmation_requested"),
                ProtectedSendTransactionStage.Verified => ("text_written", "write_verified"),
                ProtectedSendTransactionStage.ReplayAuthorized => ("replayed", "submit_requested"),
                _ => ((string Stage, string Code)?)null
            };
            var published = mapped is null || _traceStage(mapped.Value.Stage, mapped.Value.Code);
            if (published && trace.Stage == ProtectedSendTransactionStage.Verified)
            {
                _afterVerified();
            }

            return published;
        }

        public bool PublishTerminal(ProtectedSendTerminalEvidence terminal)
        {
            if (terminal.Generation != _capturedTarget.SnapshotGeneration
                || !Enum.IsDefined(terminal.Outcome))
            {
                return false;
            }

            lock (_terminalGate)
            {
                if (_terminalEvidence is not null)
                {
                    return _terminalEvidence == terminal;
                }

                _terminalEvidence = terminal;
                TerminalStatus = TerminalStatusFor(terminal.Outcome, terminal.Reason);
                if (terminal.Outcome == ProtectedSendTerminalOutcome.Blocked)
                {
                    _diagnostics["failed_closed"] = "true";
                }

                return true;
            }
        }

        internal OsInteractionResult ToOsInteractionResult(ProtectedSendTerminalResult terminal)
        {
            TerminalStatus = TerminalStatusFor(terminal.Outcome, terminal.Reason);
            if (terminal.Outcome == ProtectedSendTerminalOutcome.Blocked)
            {
                _diagnostics["failed_closed"] = "true";
            }

            var submitted = terminal.Outcome == ProtectedSendTerminalOutcome.SentSafely && terminal.TerminalPublished;
            return new OsInteractionResult(TerminalStatus, _capturedTarget.CapturedSurface, null, null, Applied, submitted, _diagnostics);
        }

        internal void Record(string status, IReadOnlyDictionary<string, string> diagnostics, bool applied = false)
        {
            TerminalStatus = status;
            Applied |= applied;
            foreach (var item in diagnostics) _diagnostics[item.Key] = item.Value;
        }

        internal void ObserveComposerAccess(bool readSucceeded)
        {
            if (readSucceeded)
            {
                _nativeVerifiedComposerAccessObserved = true;
            }

            _diagnostics["composer_access"] = _nativeVerifiedComposerAccessObserved
                ? "native_verified_composer_text_access"
                : "unavailable";
        }

        internal void ObserveComposerReadOutcome(ProtectedComposerReadResult read)
        {
            _diagnostics["composer_read_outcome"] = read.Succeeded
                ? "read_succeeded"
                : read.Surface is null
                    ? "discovery_failed"
                    : "capture_failed";
        }

        private static string TerminalStatusFor(
            ProtectedSendTerminalOutcome outcome,
            string reason)
            => outcome switch
            {
                ProtectedSendTerminalOutcome.SentSafely => OsInteractionStatusIds.Submitted,
                ProtectedSendTerminalOutcome.Cancelled => OsInteractionStatusIds.Canceled,
                ProtectedSendTerminalOutcome.Blocked when reason == nameof(TransactionFailure.ReplayFailed)
                    => OsInteractionStatusIds.ReplayUnavailable,
                ProtectedSendTerminalOutcome.Blocked when reason == nameof(TransactionFailure.ReplayUncertain)
                    => OsInteractionStatusIds.ReplayIndeterminate,
                _ => OsInteractionStatusIds.FailedClosed
            };
    }

    private sealed class ReferenceComposerTransactionSession : IProtectedSendTransactionSession
    {
        private readonly IProtectedComposerSession _session;
        private readonly ReferenceComposerTransactionEnvironment _environment;

        internal ReferenceComposerTransactionSession(IProtectedComposerSession session, string targetIdentity, ReferenceComposerTransactionEnvironment environment)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            TargetIdentity = targetIdentity;
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        public string TargetIdentity { get; }

        public TransactionStageResult Read()
        {
            var read = _session.Read();
            _environment.Record(read.Status, read.Diagnostics);
            _environment.ObserveComposerReadOutcome(read);
            _environment.ObserveComposerAccess(read.Succeeded);
            return read.Succeeded && read.Text is not null
                ? TransactionStageResult.Success(read.Text, TargetIdentity)
                : TransactionStageResult.Failed(TransactionFailure.ReadFailed);
        }

        public TransactionStageResult Revalidate()
        {
            var target = _session.Revalidate();
            _environment.Record(target.Status, target.Diagnostics);
            return target.Succeeded
                ? TransactionStageResult.Success(targetIdentity: TargetIdentity)
                : TransactionStageResult.Failed(TransactionFailure.TargetChanged);
        }

        public TransactionStageResult WriteAndVerify(string sanitizedText)
        {
            var write = _session.WriteAndVerify(sanitizedText);
            _environment.Record(write.Status, write.Diagnostics, write.Write?.Succeeded == true);
            return write.Succeeded
                ? TransactionStageResult.Success()
                : TransactionStageResult.Failed(TransactionFailure.WriteMismatch);
        }

        public TransactionStageResult Replay()
        {
            var replay = _session.Replay();
            _environment.Record(replay.Status, replay.Diagnostics);
            return replay.Succeeded
                ? TransactionStageResult.Success()
                : TransactionStageResult.Failed(replay.Status == OsInteractionStatusIds.ReplayIndeterminate
                    ? TransactionFailure.ReplayUncertain
                    : TransactionFailure.ReplayFailed);
        }
    }

    private static class ReferenceComposerKeyboardStimulus
    {
        internal static void ReplaySubmit(ReferenceComposerForm form, string sendKeysText)
        {
            ArgumentNullException.ThrowIfNull(form);
            form.Composer.Focus();
            SendKeys.SendWait(sendKeysText);
        }
    }

    private static IReadOnlyDictionary<string, string> AcceptanceDiagnostics(
        IReadOnlyDictionary<string, string> replayDiagnostics,
        IReadOnlyDictionary<string, string> operationDiagnostics,
        IReadOnlyDictionary<string, string> stimulusDiagnostics)
    {
        var diagnostics = new Dictionary<string, string>(replayDiagnostics, StringComparer.Ordinal);
        foreach (var key in new[]
        {
            "composer_access",
            "clipboard_failure",
            "sta_failure",
            "failed_closed",
            "native_capture_failure_kind",
            "native_capture_strategy"
        })
        {
            if (operationDiagnostics.TryGetValue(key, out var value))
            {
                diagnostics[key] = value;
            }
        }

        diagnostics["composer_read_outcome"] = operationDiagnostics.TryGetValue("composer_read_outcome", out var readOutcome)
            && readOutcome is "not_completed" or "discovery_failed" or "capture_failed" or "read_succeeded"
                ? readOutcome
                : operationDiagnostics.ContainsKey("composer_read_outcome")
                    ? "unavailable"
                : "not_completed";
        diagnostics.TryAdd("composer_access", "unavailable");
        diagnostics["native_capture_failure_kind"] = operationDiagnostics.TryGetValue(
                "native_capture_failure_kind",
                out var captureFailureKind)
            && captureFailureKind is "target_reacquire_failed"
                or "read_failed"
                or "empty_text"
                or "sta_failed"
                or "clipboard_failed"
                or "native_exception"
                or "none"
                ? captureFailureKind
                : "unavailable";
        diagnostics["native_capture_strategy"] = operationDiagnostics.TryGetValue(
                "native_capture_strategy",
                out var captureStrategy)
            && captureStrategy is "value_pattern"
                or "text_pattern"
                or "keyboard_fallback"
                or "unavailable"
                ? captureStrategy
                : "unavailable";
        diagnostics["stimulus_readiness_state"] = stimulusDiagnostics.TryGetValue(
                "stimulus_readiness_state",
                out var readinessState)
            && readinessState is "not_started"
                or "typing"
                or "input_ready"
                or "enter_queued"
                or "enter_dispatched"
                or "stimulus_not_ready"
                or "fixture_focus_precondition_unavailable"
                ? readinessState
                : "unavailable";
        diagnostics["stimulus_transition_sequence"] = stimulusDiagnostics.TryGetValue(
                "stimulus_transition_sequence",
                out var transitionSequence)
            && transitionSequence is "not_started"
                or "not_started,typing"
                or "not_started,typing,input_ready,enter_queued"
                or "not_started,typing,input_ready,enter_queued,enter_dispatched"
                or "not_started,typing,stimulus_not_ready"
                or "not_started,typing,input_ready,enter_queued,fixture_focus_precondition_unavailable"
                ? transitionSequence
                : "unavailable";
        diagnostics["stimulus_timeout"] = stimulusDiagnostics.TryGetValue("stimulus_timeout", out var stimulusTimeout)
            && stimulusTimeout is "true" or "false"
                ? stimulusTimeout
                : "unavailable";
        diagnostics["stimulus_terminal_reason"] = stimulusDiagnostics.TryGetValue(
                "stimulus_terminal_reason",
                out var stimulusTerminalReason)
            && stimulusTerminalReason is "none" or "stimulus_not_ready" or "fixture_focus_precondition_unavailable"
                ? stimulusTerminalReason
                : "unavailable";
        diagnostics["fixture_foreground_ready"] = stimulusDiagnostics.TryGetValue(
                "fixture_foreground_ready",
                out var fixtureForegroundReady)
            && fixtureForegroundReady is "true" or "false"
                ? fixtureForegroundReady
                : "unavailable";
        diagnostics["fixture_focused_element_ready"] = stimulusDiagnostics.TryGetValue(
                "fixture_focused_element_ready",
                out var fixtureFocusedElementReady)
            && fixtureFocusedElementReady is "true" or "false"
                ? fixtureFocusedElementReady
                : "unavailable";

        return diagnostics;
    }

    internal static IClipboardAccessBoundary CreateFixtureClipboardBoundary() =>
        new ReferenceComposerClipboardAccessBoundary();

    private sealed class ReferenceComposerStaExecutionBoundary : IStaExecutionBoundary
    {
        public StaExecutionResult<T> Execute<T>(Func<T> action)
        {
            try
            {
                var result = action();
                Application.DoEvents();
                return StaExecutionResult<T>.Success(result);
            }
            catch (Exception)
            {
                return StaExecutionResult<T>.Failure(StaFailureKind.Execution);
            }
        }
    }

    private sealed class ReferenceComposerClipboardAccessBoundary : IClipboardAccessBoundary
    {
        private IDataObject? _data = new DataObject();

        public ClipboardSnapshotCapture CaptureSnapshot() =>
            ClipboardSnapshotCapture.Success(new ReferenceComposerClipboardSnapshot(_data));

        public ClipboardBoundaryResult SetText(string text)
        {
            var data = new DataObject();
            data.SetText(text, TextDataFormat.UnicodeText);
            _data = data;
            return ClipboardBoundaryResult.Success();
        }

        public ClipboardBoundaryResult Clear()
        {
            _data = null;
            return ClipboardBoundaryResult.Success();
        }

        public ClipboardTextRead ReadUnicodeText()
        {
            if (_data?.GetDataPresent(DataFormats.UnicodeText, autoConvert: false) != true)
            {
                return ClipboardTextRead.Failure();
            }

            return _data.GetData(DataFormats.UnicodeText, autoConvert: false) is string text
                ? ClipboardTextRead.Success(text)
                : ClipboardTextRead.Failure();
        }

        public ClipboardBoundaryResult Restore(IClipboardSnapshot snapshot)
        {
            if (snapshot is not ReferenceComposerClipboardSnapshot captured)
            {
                return ClipboardBoundaryResult.Failure();
            }

            _data = captured.Data;
            return ClipboardBoundaryResult.Success();
        }

        private sealed record ReferenceComposerClipboardSnapshot(IDataObject? Data) : IClipboardSnapshot;
    }

    /// <summary>
    /// Fixture-only observer around the production replay delegate. Controlled
    /// unavailable and partial modes remain explicit negative controls; the
    /// positive mode succeeds only after one real fixture submit is observed.
    /// </summary>
    private sealed class ReferenceComposerReplayBoundary : IVerifiedComposerReplay
    {
        private readonly ReferenceComposerForm _form;
        private readonly ReferenceComposerReplayMode _mode;
        private readonly IVerifiedComposerReplay _productionReplay = new NativeVerifiedComposerReplay();
        private static readonly TimeSpan SubmitObservationTimeout = TimeSpan.FromSeconds(2);

        public ReferenceComposerReplayBoundary(
            ReferenceComposerForm form,
            ReferenceComposerReplayMode mode)
        {
            _form = form;
            _mode = mode;
        }

        public IReadOnlyDictionary<string, string> Diagnostics { get; private set; } =
            new Dictionary<string, string>();

        public VerifiedComposerReplayResult Replay(AutomationElement? element, string sendKeysText)
        {
            if (_mode != ReferenceComposerReplayMode.Available)
            {
                var outcome = _mode == ReferenceComposerReplayMode.Unavailable ? "unavailable" : "partial";
                Diagnostics = new Dictionary<string, string>
                {
                    ["replay_outcome"] = outcome,
                    ["modifiers_released"] = "true",
                    ["delegate_replay_succeeded"] = "false",
                    ["submit_observed"] = "false"
                };
                return new VerifiedComposerReplayResult(
                    false,
                    _mode == ReferenceComposerReplayMode.Unavailable
                        ? OsInteractionStatusIds.ReplayUnavailable
                        : OsInteractionStatusIds.ReplayIndeterminate,
                    Diagnostics);
            }

            var previousCount = _form.SentCount;
            var foregroundMatchesTargetBefore = ForegroundMatchesTarget(element);
            var focusedElementMatchesTargetBefore = FocusedElementMatchesTarget(element);
            var replayInvoked = true;
            var replayDelegateReturned = false;
            var delegated = _productionReplay.Replay(element, sendKeysText);
            replayDelegateReturned = true;
            var foregroundMatchesTargetAfter = ForegroundMatchesTarget(element);
            var focusedElementMatchesTargetAfter = FocusedElementMatchesTarget(element);
            var submitObserved = delegated.Succeeded
                && _form.WaitForExactlyOneAdditionalSubmit(previousCount, SubmitObservationTimeout);
            var diagnostics = new Dictionary<string, string>(delegated.Diagnostics, StringComparer.Ordinal)
            {
                ["replay_invoked"] = replayInvoked.ToString().ToLowerInvariant(),
                ["replay_delegate_returned"] = replayDelegateReturned.ToString().ToLowerInvariant(),
                ["foreground_matches_target_before"] = foregroundMatchesTargetBefore.ToString().ToLowerInvariant(),
                ["focused_element_matches_target_before"] = focusedElementMatchesTargetBefore.ToString().ToLowerInvariant(),
                ["foreground_matches_target_after"] = foregroundMatchesTargetAfter.ToString().ToLowerInvariant(),
                ["focused_element_matches_target_after"] = focusedElementMatchesTargetAfter.ToString().ToLowerInvariant(),
                ["delegate_replay_succeeded"] = delegated.Succeeded.ToString().ToLowerInvariant(),
                ["submit_observed"] = submitObserved.ToString().ToLowerInvariant()
            };

            if (delegated.Succeeded && !submitObserved)
            {
                diagnostics["replay_outcome"] = "replay_not_observed";
            }

            Diagnostics = diagnostics;
            return delegated.Succeeded && !submitObserved
                ? new VerifiedComposerReplayResult(false, OsInteractionStatusIds.ReplayIndeterminate, Diagnostics)
                : delegated with { Diagnostics = Diagnostics };
        }

        private static bool ForegroundMatchesTarget(AutomationElement? element)
        {
            try
            {
                var targetHandle = element is null
                    ? IntPtr.Zero
                    : new IntPtr(element.Current.NativeWindowHandle);
                var targetRoot = targetHandle == IntPtr.Zero
                    ? IntPtr.Zero
                    : NativeMethods.GetAncestor(targetHandle, 2);
                return targetRoot != IntPtr.Zero
                    && targetRoot == NativeMethods.GetForegroundWindow();
            }
            catch
            {
                return false;
            }
        }

        private static bool FocusedElementMatchesTarget(AutomationElement? element)
        {
            try
            {
                var focused = AutomationElement.FocusedElement;
                return element is not null
                    && focused is not null
                    && Automation.Compare(focused, element);
            }
            catch
            {
                return false;
            }
        }

        private static class NativeMethods
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern IntPtr GetForegroundWindow();

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
        }
    }

    private sealed class ReferenceComposerTargetController
    {
        private readonly ReferenceComposerForm? _replacement;
        private readonly ReferenceComposerTargetChangeMode _mode;
        private ReferenceComposerForm _active;

        public ReferenceComposerTargetController(
            ReferenceComposerForm original,
            ReferenceComposerForm? replacement,
            ReferenceComposerTargetChangeMode mode)
        {
            _replacement = replacement;
            _mode = mode;
            _active = original;
        }

        public ReferenceComposerForm GetActiveForm() => Volatile.Read(ref _active);

        public void ChangeAfterApproval()
        {
            if (_mode == ReferenceComposerTargetChangeMode.BeforeWrite)
            {
                ChangeTarget();
            }
        }

        public void ChangeBeforeReplay()
        {
            if (_mode == ReferenceComposerTargetChangeMode.BeforeReplay)
            {
                ChangeTarget();
            }
        }

        private void ChangeTarget() => Volatile.Write(
            ref _active,
            _replacement ?? throw new InvalidOperationException("Replacement fixture is unavailable."));
    }
}

internal static class ReferenceComposerAcceptanceSmokeRunner
{
    internal static ReferenceComposerAcceptanceSmokeReport Run(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);
        try
        {
            var safe = ReferenceComposerAcceptanceRunner.Run(
                new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
                "A harmless local prompt",
                ReferenceComposerDecision.Approve,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary);
            var sensitive = ReferenceComposerAcceptanceRunner.Run(
                new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
                "Connect to 192.168.10.25",
                ReferenceComposerDecision.Approve,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary);
            var cancelled = ReferenceComposerAcceptanceRunner.Run(
                new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
                "Connect to 192.168.10.25",
                ReferenceComposerDecision.Cancel,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary);
            var repeated = ReferenceComposerAcceptanceRunner.Run(
                new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
                "A harmless local prompt",
                ReferenceComposerDecision.Approve,
                clipboardBoundaryFactory: ReferenceComposerAcceptanceRunner.CreateFixtureClipboardBoundary);

            return new ReferenceComposerAcceptanceSmokeReport(
                SafePromptPassed: safe.HookStarted
                    && safe.OriginalInputSuppressed
                    && safe.Submitted
                    && safe.Trace.LastOrDefault()?.Stage == "sent_safely"
                    && safe.SentTexts.Count == 1,
                SensitivePromptPassed: sensitive.Submitted
                    && sensitive.Trace.Any(entry => entry.Stage == "overlay_foreground_confirmed")
                    && sensitive.SentTexts.Count == 1
                    && !sensitive.SentTexts[0].Contains("192.168.10.25", StringComparison.Ordinal),
                CancellationPassed: !cancelled.Submitted
                    && cancelled.SentTexts.Count == 0
                    && cancelled.Trace.LastOrDefault()?.Stage == "terminal_blocked",
                RepeatedCleanupPassed: repeated.HookStarted
                    && repeated.Submitted
                    && repeated.SentTexts.Count == 1,
                Status: "completed");
        }
        catch (Exception exception)
        {
            return new ReferenceComposerAcceptanceSmokeReport(
                false,
                false,
                false,
                false,
                $"failed_{exception.GetType().Name.ToLowerInvariant()}");
        }
    }
}
