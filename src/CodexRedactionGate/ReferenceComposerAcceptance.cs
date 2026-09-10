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

/// <summary>
/// Local-only acceptance fixture. Its input capability is compiled into the
/// hook host and cannot be persisted or selected as an AI client profile.
/// </summary>
internal static class ReferenceComposerAcceptanceRunner
{
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

        // The fixture never seeds the composer TextBox. The prompt reaches the
        // composer only through the production user gesture, so the fixture
        // cannot act as a fallback owner of the read/write/replay side effect.
        var evidenceLevel = ReferenceComposerEvidenceLevel.Unavailable;
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
                IReadOnlyDictionary<string, string> operationDiagnostics = new Dictionary<string, string>();
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
                        evidenceLevel = environment.EvidenceLevel;
                        operationDiagnostics = environment.Diagnostics;
                        return environment.ToOsInteractionResult(terminal);
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

                    // Only the production composer access path can produce
                    // acceptance evidence; the fixture never substitutes a write.
                    evidenceLevel = ReferenceComposerEvidenceLevelTokens.SelectForPublication(evidenceLevel);
                    report = new ReferenceComposerAcceptanceReport(
                        hookStarted,
                        dispatch.SuppressOriginalInput,
                        state.LastStatus,
                        state.LastSubmitted,
                        composer.SentTexts.ToArray(),
                        trace.ToArray(),
                        AcceptanceDiagnostics(replay.Diagnostics, operationDiagnostics),
                        CleanupPassed: false,
                        EvidenceLevel: evidenceLevel);
                    completed.Set();
                    if (composer.IsHandleCreated && !composer.IsDisposed)
                    {
                        composer.BeginInvoke(new Action(composer.Close));
                    }
                };

                composer.Shown += (_, _) =>
                {
                    replacementComposer.CreateControl();
                    replacementComposer.Composer.CreateControl();
                    _ = replacementComposer.Handle;
                    _ = replacementComposer.Composer.Handle;
                    ReferenceComposerKeyboardStimulus.TypePrompt(composer, prompt);
                    using var source = hookHost.OpenReferenceOnlyInputSourceForAcceptance(composer.Handle);
                    dispatch = source.DispatchKeyboard(new NativeKeyGesture(
                        "Enter",
                        TargetWindow: composer.Handle,
                        TargetProcessId: (uint)Environment.ProcessId));
                };

                try
                {
                    Application.Run(composer);
                }
                finally
                {
                    controller.Stop();
                }
                report ??= new ReferenceComposerAcceptanceReport(
                    hookStarted,
                    dispatch.SuppressOriginalInput,
                    OsInteractionStatusIds.FailedClosed,
                    Submitted: false,
                    composer.SentTexts.ToArray(),
                    controller.State.ProtectedSendAttemptTrace?.ToArray() ?? Array.Empty<ProtectedSendTraceEntry>(),
                    AcceptanceDiagnostics(replay.Diagnostics, operationDiagnostics),
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
        if (!completed.Wait(TimeSpan.FromSeconds(15)))
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
    /// Minimal reference composer fixture. It exposes only deterministic stimuli
    /// (a keyboard gesture and the "was a send recorded" fact) plus the reference
    /// target. It deliberately does not seed the prompt into the TextBox, does not
    /// expose the TextBox content for direct reads and does not replay a send on
    /// its own, so the fixture can never act as a fallback owner of the read,
    /// write or replay side effect. All composer side effects go through
    /// <see cref="NativeVerifiedComposerTextAccess"/>.
    /// </summary>
    private sealed class ReferenceComposerForm : Form
    {
        private readonly List<string> _sentTexts = new();

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
            Shown += (_, _) => Composer.Focus();
            KeyDown += OnKeyDown;
        }

        public TextBox Composer { get; }

        public IReadOnlyList<string> SentTexts => _sentTexts;

        private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
        {
            if (eventArgs.KeyCode != Keys.Enter || eventArgs.Control || eventArgs.Alt || eventArgs.Shift)
            {
                return;
            }

            eventArgs.SuppressKeyPress = true;
            _sentTexts.Add(Composer.Text);
        }
    }

    private sealed class ReferenceComposerTransactionEnvironment : IProtectedSendTransactionEnvironment
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
        private readonly Dictionary<string, string> _diagnostics = new(StringComparer.Ordinal);

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
            Action afterVerified)
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
                ProtectedSendTransactionStage.ReplayAuthorized => ("send_injected", "submit_requested"),
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
            TerminalStatus = TerminalStatusFor(terminal.Outcome);
            if (terminal.Outcome == ProtectedSendTerminalOutcome.Blocked)
            {
                _diagnostics["failed_closed"] = "true";
            }

            var stage = terminal.Outcome == ProtectedSendTerminalOutcome.SentSafely ? "sent_safely" : "terminal_blocked";
            return _traceStage(stage, TerminalStatus);
        }

        internal OsInteractionResult ToOsInteractionResult(ProtectedSendTerminalResult terminal)
        {
            TerminalStatus = TerminalStatusFor(terminal.Outcome);
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

        private static string TerminalStatusFor(ProtectedSendTerminalOutcome outcome)
            => outcome switch
            {
                ProtectedSendTerminalOutcome.SentSafely => OsInteractionStatusIds.Submitted,
                ProtectedSendTerminalOutcome.Cancelled => OsInteractionStatusIds.Canceled,
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
        internal static void TypePrompt(ReferenceComposerForm form, string prompt)
        {
            ArgumentNullException.ThrowIfNull(form);
            ArgumentNullException.ThrowIfNull(prompt);
            form.Composer.Focus();
            SendKeys.SendWait(ToSendKeys(prompt));
        }

        internal static void ReplaySubmit(ReferenceComposerForm form, string sendKeysText)
        {
            ArgumentNullException.ThrowIfNull(form);
            form.Composer.Focus();
            SendKeys.SendWait(sendKeysText);
        }

        private static string ToSendKeys(string text)
        {
            var escaped = text.Replace("{", "{{}", StringComparison.Ordinal)
                .Replace("}", "{}}", StringComparison.Ordinal)
                .Replace("+", "{+}", StringComparison.Ordinal)
                .Replace("^", "{^}", StringComparison.Ordinal)
                .Replace("%", "{%}", StringComparison.Ordinal)
                .Replace("~", "{~}", StringComparison.Ordinal)
                .Replace("(", "{(}", StringComparison.Ordinal)
                .Replace(")", "{)}", StringComparison.Ordinal)
                .Replace("[", "{[}", StringComparison.Ordinal)
                .Replace("]", "{]}", StringComparison.Ordinal);
            return escaped.Replace("\r\n", "+{ENTER}", StringComparison.Ordinal)
                .Replace("\n", "+{ENTER}", StringComparison.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, string> AcceptanceDiagnostics(
        IReadOnlyDictionary<string, string> replayDiagnostics,
        IReadOnlyDictionary<string, string> operationDiagnostics)
    {
        var diagnostics = new Dictionary<string, string>(replayDiagnostics, StringComparer.Ordinal);
        foreach (var key in new[] { "composer_access", "clipboard_failure", "sta_failure", "failed_closed" })
        {
            if (operationDiagnostics.TryGetValue(key, out var value))
            {
                diagnostics[key] = value;
            }
        }

        diagnostics.TryAdd("composer_access", "unavailable");

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
    /// The single fixture-side replay stimulus. It records the send that the
    /// production access path completed and exposes only the raw-free fact that a
    /// send happened; it never reads or writes the composer `TextBox` and never
    /// replays a send on its own.
    /// </summary>
    private sealed class ReferenceComposerReplayBoundary : IVerifiedComposerReplay
    {
        private readonly ReferenceComposerForm _form;
        private readonly ReferenceComposerReplayMode _mode;

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
            var outcome = _mode switch
            {
                ReferenceComposerReplayMode.Unavailable => "unavailable",
                ReferenceComposerReplayMode.Partial => "partial",
                _ => "completed"
            };
            Diagnostics = new Dictionary<string, string>
            {
                ["replay_outcome"] = outcome,
                ["modifiers_released"] = "true"
            };

            if (_mode != ReferenceComposerReplayMode.Available)
            {
                return new VerifiedComposerReplayResult(
                    false,
                    _mode == ReferenceComposerReplayMode.Unavailable
                        ? OsInteractionStatusIds.ReplayUnavailable
                        : OsInteractionStatusIds.ReplayIndeterminate,
                    Diagnostics);
            }

            ReferenceComposerKeyboardStimulus.ReplaySubmit(_form, sendKeysText);
            return new VerifiedComposerReplayResult(true, OsInteractionStatusIds.Submitted, Diagnostics);
        }
    }

    private sealed class ReferenceComposerTargetController
    {
        private readonly ReferenceComposerForm _replacement;
        private readonly ReferenceComposerTargetChangeMode _mode;
        private ReferenceComposerForm _active;

        public ReferenceComposerTargetController(
            ReferenceComposerForm original,
            ReferenceComposerForm replacement,
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

        private void ChangeTarget() => Volatile.Write(ref _active, _replacement);
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
