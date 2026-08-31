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

internal sealed record ReferenceComposerAcceptanceReport(
    bool HookStarted,
    bool OriginalInputSuppressed,
    string TerminalStatus,
    bool Submitted,
    IReadOnlyList<string> SentTexts,
    IReadOnlyList<ProtectedSendTraceEntry> Trace,
    IReadOnlyDictionary<string, string> ReplayDiagnostics,
    bool CleanupPassed);

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
        Func<IClipboardAccessBoundary>? clipboardBoundaryFactory = null)
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
                CleanupPassed: false);
        }

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
                using var composer = new ReferenceComposerForm(prompt);
                using var replacementComposer = new ReferenceComposerForm(string.Empty);
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
                        var targetAwareDiscovery = new CapturedTargetSurfaceDiscovery(discovery, target);
                        var clipboard = clipboardBoundaryFactory?.Invoke();
                        var textAccess = new NativeVerifiedComposerTextAccess(
                            targetAwareDiscovery.DiscoverActiveSurface,
                            replay,
                            new ReferenceComposerStaExecutionBoundary(),
                            clipboard: clipboard);
                        var adapter = new WindowsVerifiedComposerSurfaceAdapter(textAccess);
                        var orchestrator = new OsInteractionOrchestrator(
                            sanitizer,
                            new ProtectedComposerSessionFactory(
                                targetAwareDiscovery,
                                adapter,
                                adapter,
                                new VerifiedSubmitBindingAction(adapter, profile)),
                            overlay);
                        Func<string, string, bool> acceptanceTrace = (stage, resultCode) =>
                        {
                            var traced = traceStage(stage, resultCode);
                            if (traced && stage == "text_written")
                            {
                                targets.ChangeBeforeReplay();
                            }

                            return traced;
                        };
                        var result = orchestrator.RunOnce(
                            OsInteractionRunOptions.ConfirmAndSend,
                            acceptanceTrace,
                            executionGuard,
                            executionLease);
                        operationDiagnostics = result.Diagnostics;
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

                    report = new ReferenceComposerAcceptanceReport(
                        hookStarted,
                        dispatch.SuppressOriginalInput,
                        state.LastStatus,
                        state.LastSubmitted,
                        composer.SentTexts.ToArray(),
                        trace.ToArray(),
                        AcceptanceDiagnostics(replay.Diagnostics, operationDiagnostics),
                        CleanupPassed: false);
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
                    CleanupPassed: false);
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
    }

    private sealed class ReferenceComposerForm : Form
    {
        private readonly List<string> _sentTexts = new();

        public ReferenceComposerForm(string prompt)
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
                Dock = DockStyle.Fill,
                Text = prompt
            };
            Controls.Add(Composer);
            Shown += (_, _) => Composer.Focus();
            KeyDown += OnKeyDown;
        }

        public TextBox Composer { get; }

        public IReadOnlyList<string> SentTexts => _sentTexts;

        public void SubmitFromAcceptance()
        {
            _sentTexts.Add(Composer.Text);
        }

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

    private static IReadOnlyDictionary<string, string> AcceptanceDiagnostics(
        IReadOnlyDictionary<string, string> replayDiagnostics,
        IReadOnlyDictionary<string, string> operationDiagnostics)
    {
        var diagnostics = new Dictionary<string, string>(replayDiagnostics, StringComparer.Ordinal)
        {
            ["composer_access"] = "native_verified_composer_text_access"
        };
        foreach (var key in new[] { "clipboard_failure", "sta_failure", "failed_closed" })
        {
            if (operationDiagnostics.TryGetValue(key, out var value))
            {
                diagnostics[key] = value;
            }
        }

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

            _form.Composer.Focus();
            _form.SubmitFromAcceptance();
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
