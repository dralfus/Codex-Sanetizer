using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CodexRedactionGate;

internal sealed record SecondInstanceNotification(
    string Title,
    string Message,
    int DisplayMilliseconds,
    bool ActivationSucceeded);

internal interface IExistingTrayInstanceActivator
{
    bool TryActivate(string instanceId, bool useGlobalMutex);
}

internal sealed class WindowsExistingTrayInstanceActivator : IExistingTrayInstanceActivator
{
    public static WindowsExistingTrayInstanceActivator Instance { get; } = new();

    private WindowsExistingTrayInstanceActivator()
    {
    }

    public bool TryActivate(string instanceId, bool useGlobalMutex)
    {
        return SingleInstanceEnforcement.ActivateExistingInstance(instanceId, useGlobalMutex);
    }
}

public static class WindowsTrayApp
{
    public static int Run(ISanitizer sanitizer)
    {
        return Run(sanitizer, DefaultStorageLayout.CreateDefault(), useGlobalMutex: false);
    }

    internal static int Run(ISanitizer sanitizer, DefaultStorageLayout layout)
    {
        return Run(sanitizer, layout, useGlobalMutex: false);
    }

    internal static int Run(ISanitizer sanitizer, DefaultStorageLayout layout, bool useGlobalMutex)
    {
        return Run(sanitizer, layout, useGlobalMutex, Application.Run, secondInstanceNotificationSettings: null);
    }

    internal static int RunRecoveryRequired(DefaultStorageLayout layout, bool useGlobalMutex)
    {
        return Run(
            new RecoveryRequiredSanitizer(),
            layout,
            useGlobalMutex,
            Application.Run,
            secondInstanceNotificationSettings: null,
            localProtectionStatus: LocalProtectionRecovery.RecoveryRequiredCode,
            recoveredRuntimeFactory: () => CreateResidentProtectionRuntime(Sanitizer.CreateProduction(layout), layout));
    }

    internal static int Run(
        ISanitizer sanitizer,
        DefaultStorageLayout layout,
        bool useGlobalMutex,
        Action<WindowsTrayApplicationContext> runMessageLoop,
        SingleInstanceNotificationSettings? secondInstanceNotificationSettings,
        Action<SecondInstanceNotification>? secondInstanceNotificationPresenter = null,
        string localProtectionStatus = LocalProtectionRecovery.ReadyCode,
        Func<ResidentProtectionRuntime>? recoveredRuntimeFactory = null,
        IExistingTrayInstanceActivator? existingInstanceActivator = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(runMessageLoop);

        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        if (useGlobalMutex && !SingleInstanceEnforcement.CanUseGlobalMutex)
        {
            return 1;
        }

        var activator = existingInstanceActivator ?? WindowsExistingTrayInstanceActivator.Instance;

        // Single instance enforcement - second launch should activate existing instance and exit
        if (SingleInstanceEnforcement.IsAnotherInstanceRunning("tray", useGlobalMutex))
        {
            var activationSucceeded = activator.TryActivate("tray", useGlobalMutex);
            NotifyBlockedSecondInstance(
                secondInstanceNotificationSettings,
                activationSucceeded,
                secondInstanceNotificationPresenter);
            return 0; // Exit cleanly - existing instance will handle everything
        }

        using var enforcement = new SingleInstanceEnforcement("tray", useGlobalMutex);
        if (!enforcement.IsFirstInstance)
        {
            var activationSucceeded = activator.TryActivate("tray", useGlobalMutex);
            NotifyBlockedSecondInstance(
                secondInstanceNotificationSettings,
                activationSucceeded,
                secondInstanceNotificationPresenter);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new WindowsTrayApplicationContext(
            CreateController(sanitizer, layout),
            layout,
            new WindowsTrayLocalCommandLauncher(),
            new MessageBoxTrayProtectionDisableConfirmation(),
            enforcement,
            () => CreateNativeSubmitRuntimeSet(sanitizer, layout),
            localProtectionStatus: localProtectionStatus,
            recoveredRuntimeFactory: recoveredRuntimeFactory ?? (() => CreateResidentProtectionRuntime(sanitizer, layout)),
            candidateNativeSubmitRuntimeFactory: profiles => CreateNativeSubmitRuntimeSet(sanitizer, layout, profiles));
        runMessageLoop(context);
        return 0;
    }

    private static void NotifyBlockedSecondInstance(
        SingleInstanceNotificationSettings? configuredSettings,
        bool activationSucceeded,
        Action<SecondInstanceNotification>? presenter)
    {
        if (!ShouldNotifySecondInstance())
        {
            return;
        }

        // The second process exits after this handoff, so one load is both a cache
        // and the complete configuration lifetime for that launch.
        var settings = configuredSettings ?? SingleInstanceNotificationSettings.Load();
        ShowAlreadyRunningNotification(
            settings,
            activationSucceeded,
            presenter,
            forceVisible: !activationSucceeded);
    }

    private static void ShowAlreadyRunningNotification(
        SingleInstanceNotificationSettings settings,
        bool activationSucceeded,
        Action<SecondInstanceNotification>? presenter,
        bool forceVisible)
    {
        var notificationDetails = CreateSecondInstanceNotification(settings, activationSucceeded, forceVisible);
        if (notificationDetails is null)
        {
            return;
        }

        if (presenter is not null)
        {
            presenter(notificationDetails);
            return;
        }

        using var notification = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            BalloonTipTitle = notificationDetails.Title,
            BalloonTipText = notificationDetails.Message
        };
        notification.ShowBalloonTip(notificationDetails.DisplayMilliseconds);
        var until = DateTime.UtcNow.AddMilliseconds(notificationDetails.DisplayMilliseconds);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
        }
    }

    internal static SecondInstanceNotification? CreateSecondInstanceNotification(
        SingleInstanceNotificationSettings settings,
        bool activationSucceeded,
        bool forceVisible = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if ((!settings.Enabled || settings.Type == "none") && !forceVisible)
        {
            return null;
        }

        return new SecondInstanceNotification(
            AppStrings.Get("ProductName"),
            AppStrings.Get("AlreadyRunning"),
            DisplayMilliseconds: 3000,
            ActivationSucceeded: activationSucceeded);
    }

    internal static bool ShouldNotifySecondInstance()
    {
        // Foregrounding the resident's hidden activation window is not a user-visible outcome.
        return true;
    }

    internal static TrayProtectionController CreateController(ISanitizer sanitizer, DefaultStorageLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);

        var settings = HotkeySettingsStore.Load(layout ?? DefaultStorageLayout.CreateDefault());
        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        var residentRuntime = CreateResidentProtectionRuntime(sanitizer, resolvedLayout);
        var runtimeSet = residentRuntime.NativeSubmitRuntimeSet;
        var runtime = runtimeSet?.Runtimes.FirstOrDefault();
        var nativeProfile = runtime?.Profile;
        var activeSurfaceDiscovery = WindowsFocusedComposerDiscovery.CreateDefault();

        return new TrayProtectionController(
            settings.Usable
                ? new WindowsTrayHotkeyHost(settings.Settings.ProtectionHotkey)
                : new UnavailableTrayHotkeyHost(settings.Settings.ProtectionHotkey.Binding, settings.Code),
            residentRuntime.ApplyOnlyRunner,
            runtime?.HookHost,
            runtime?.Controller,
            nativeProfile,
            storageLayout: resolvedLayout,
            sendControlDiscovery: WindowsSendControlDiscovery.CreateDefault(resolvedLayout),
            nativeSubmitRuntimes: runtimeSet?.Runtimes,
            activeSurfaceDiscovery: activeSurfaceDiscovery.DiscoverActiveSurface,
            residentRuntimeOwner: residentRuntime.ApplyOnlyResourceOwner,
            nativeSubmitRuntimeOwner: runtimeSet?.ResourceOwner);
    }

    internal static ResidentProtectionRuntime CreateResidentProtectionRuntime(
        ISanitizer sanitizer,
        DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);

        var liveAdapter = new WindowsVerifiedComposerSurfaceAdapter();
        var confirmationOverlay = new WindowsConfirmationOverlay();
        var orchestrator = new OsInteractionOrchestrator(
            sanitizer,
            WindowsFocusedComposerDiscovery.CreateDefault(),
            liveAdapter,
            liveAdapter,
            liveAdapter,
            confirmationOverlay);
        return new ResidentProtectionRuntime(
            () => orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly),
            CreateNativeSubmitRuntimeSet(sanitizer, layout),
            confirmationOverlay);
    }

    internal static NativeSubmitRuntime? CreateNativeSubmitRuntime(ISanitizer sanitizer, DefaultStorageLayout layout)
    {
        return CreateNativeSubmitRuntimeSet(sanitizer, layout)?.Runtimes.FirstOrDefault();
    }

    internal static NativeSubmitRuntimeSet? CreateNativeSubmitRuntimeSet(
        ISanitizer sanitizer,
        DefaultStorageLayout layout,
        IReadOnlyList<SubmitBindingProfile>? profilesOverride = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);

        var profiles = profilesOverride ?? ResolveNativeProfilesForProtection(layout);
        if (profiles.Count == 0)
        {
            return null;
        }

        var hookHost = new WindowsNativeSubmitHookHost(profiles);
        var activeSurfaceDiscovery = WindowsFocusedComposerDiscovery.CreateDefault();
        var confirmationOverlay = new WindowsConfirmationOverlay();
        var runtimes = profiles.Select(nativeProfile =>
        {
            var controller = new NativeSubmitInterceptionController(
                nativeProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: activeSurfaceDiscovery.DiscoverActiveSurface,
                firstRunSetupController: new FirstRunSetupController(),
                setupLayout: layout);
            OsInteractionResult RunConfirmAndSend(
                NativeSubmitTargetIdentity? target,
                Func<string, string, bool>? traceStage,
                Func<bool>? executionGuard = null,
                Func<IDisposable?>? executionLease = null)
            {
                var nativeSubmitAdapter = new WindowsVerifiedComposerSurfaceAdapter();
                IActiveTextSurfaceDiscovery composerDiscovery = target is null
                    ? WindowsFocusedComposerDiscovery.CreateDefault()
                    : new CapturedTargetSurfaceDiscovery(
                        WindowsFocusedComposerDiscovery.CreateDefault(),
                        target);
                var nativeSubmitOrchestrator = new OsInteractionOrchestrator(
                    sanitizer,
                    composerDiscovery,
                    nativeSubmitAdapter,
                    nativeSubmitAdapter,
                    new VerifiedSubmitBindingAction(nativeSubmitAdapter, nativeProfile),
                    confirmationOverlay);
                return nativeSubmitOrchestrator.RunOnce(
                    OsInteractionRunOptions.ConfirmAndSend,
                    traceStage,
                    executionGuard,
                    executionLease);
            }

            return new NativeSubmitRuntime(
                hookHost,
                controller,
                nativeProfile,
                ResidentTargetTracedRunner: (target, traceStage, executionGuard, executionLease) => RunConfirmAndSend(
                    target: target,
                    traceStage: traceStage,
                    executionGuard: executionGuard,
                    executionLease: executionLease));
        }).ToArray();
        return new NativeSubmitRuntimeSet(
            hookHost,
            runtimes,
            confirmationOverlay,
            confirmationOverlay.CancelActiveConfirmation);
    }

    internal static SubmitBindingProfile? ResolveNativeProfileForProtection(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return ResolveNativeProfilesForProtection(layout).FirstOrDefault();
    }

    internal static IReadOnlyList<SubmitBindingProfile> ResolveNativeProfilesForProtection(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var profiles = SubmitBindingProfileStore.Load(layout).Profiles;
        if (profiles.Count == 0)
        {
            return new[]
            {
                FirstRunSetupController.CreateDefaultSetupProfile("codex-desktop")!,
                FirstRunSetupController.CreateDefaultSetupProfile("chatgpt-desktop")!
            };
        }

        return profiles.Where(profile => profile.Enabled).ToArray();
    }
}

internal sealed class UnavailableTrayHotkeyHost : ITrayHotkeyHost
{
    public UnavailableTrayHotkeyHost(HotkeyBinding binding, string errorCode)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        LastErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? "hotkey_unavailable"
            : errorCode;
    }

    public HotkeyBinding Binding { get; }

    public string? LastErrorCode { get; }

    public bool Start(Action onTriggered)
    {
        ArgumentNullException.ThrowIfNull(onTriggered);
        return false;
    }

    public void Stop()
    {
    }
}

internal sealed class TrayRemediationActionExecutor
{
    private readonly Action<Action> _backgroundWorkQueue;
    private readonly Action<Action> _uiDispatcher;
    private int _inProgress;

    public TrayRemediationActionExecutor(
        Action<Action> backgroundWorkQueue,
        Action<Action> uiDispatcher)
    {
        _backgroundWorkQueue = backgroundWorkQueue ?? throw new ArgumentNullException(nameof(backgroundWorkQueue));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public bool TryRun<T>(
        Func<T> backgroundWork,
        Action<T> completeOnUi,
        Action<Exception> captureWorkerFailure,
        Action<T> onUiDispatcherUnavailable)
    {
        ArgumentNullException.ThrowIfNull(backgroundWork);
        ArgumentNullException.ThrowIfNull(completeOnUi);
        ArgumentNullException.ThrowIfNull(captureWorkerFailure);
        ArgumentNullException.ThrowIfNull(onUiDispatcherUnavailable);

        if (Interlocked.Exchange(ref _inProgress, 1) != 0)
        {
            return false;
        }

        try
        {
            _backgroundWorkQueue(() => RunBackgroundWork(
                backgroundWork,
                completeOnUi,
                captureWorkerFailure,
                onUiDispatcherUnavailable));
        }
        catch (Exception exception)
        {
            try
            {
                captureWorkerFailure(exception);
            }
            finally
            {
                Release();
            }
        }

        return true;
    }

    private void RunBackgroundWork<T>(
        Func<T> backgroundWork,
        Action<T> completeOnUi,
        Action<Exception> captureWorkerFailure,
        Action<T> onUiDispatcherUnavailable)
    {
        T result;
        try
        {
            result = backgroundWork();
        }
        catch (Exception exception)
        {
            try
            {
                captureWorkerFailure(exception);
            }
            finally
            {
                Release();
            }

            return;
        }

        try
        {
            _uiDispatcher(() =>
            {
                try
                {
                    completeOnUi(result);
                }
                catch (Exception exception)
                {
                    captureWorkerFailure(exception);
                }
                finally
                {
                    Release();
                }
            });
        }
        catch (Exception exception)
        {
            try
            {
                onUiDispatcherUnavailable(result);
            }
            catch (Exception cleanupException)
            {
                captureWorkerFailure(cleanupException);
            }
            finally
            {
                try
                {
                    captureWorkerFailure(exception);
                }
                finally
                {
                    Release();
                }
            }
        }
    }

    private void Release()
    {
        Volatile.Write(ref _inProgress, 0);
    }
}

internal sealed class WindowsTrayApplicationContext : ApplicationContext
{
    private readonly TrayProtectionController _controller;
    private readonly ITrayLocalCommandLauncher _commandLauncher;
    private readonly ITrayProtectionDisableConfirmation _disableConfirmation;
    private readonly DefaultStorageLayout _layout;
    private readonly string _buildVersion;
    private readonly LocalCrashDiagnostics _crashDiagnostics;
    private readonly NotifyIcon _notifyIcon;
    private readonly Form _activationWindow;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _emergencyBypassItem;
    private readonly ToolStripMenuItem _repairLocalProtectionItem;
    private readonly SingleInstanceEnforcement? _singleInstanceEnforcement;
    private Func<NativeSubmitRuntimeSet?>? _nativeSubmitRuntimeFactory;
    private readonly Func<IReadOnlyList<SubmitBindingProfile>, NativeSubmitRuntimeSet?> _candidateNativeSubmitRuntimeFactory;
    private readonly Func<IFirstRunSetupController> _firstRunSetupControllerFactory;
    private readonly Action<FirstRunSetupResult?>? _firstRunSetupCompleted;
    private readonly Func<ResidentProtectionRuntime> _recoveredRuntimeFactory;
    private readonly Func<LocalProtectionRecoveryResult> _localProtectionRecovery;
    private readonly Func<bool> _recoveryConfirmation;
    private readonly Action<string, MessageBoxIcon> _recoveryMessagePresenter;
    private readonly Action<Action> _backgroundWorkQueue;
    private readonly Action<Action> _uiDispatcher;
    private readonly TrayRemediationActionExecutor _remediationActionExecutor;
    private LocalProtectionStatusForm? _localProtectionStatusForm;
    private int _firstRunSetupScheduled;

    internal bool IsTrayIconVisible => _notifyIcon.Visible;

    internal string TrayTooltipText => _notifyIcon.Text ?? string.Empty;

    internal string TrayStatusText => _statusItem.Text ?? string.Empty;

    internal string EmergencyBypassMenuText => _emergencyBypassItem.Text ?? string.Empty;

    internal bool IsLocalProtectionStatusOpen => _localProtectionStatusForm is { IsDisposed: false, Visible: true };

    internal LocalProtectionStatusForm? LocalProtectionStatusForm => _localProtectionStatusForm;

    internal bool IsNativeSubmitHookReady => _controller.IsNativeSubmitHookReady;

    public WindowsTrayApplicationContext(TrayProtectionController controller)
        : this(controller, DefaultStorageLayout.CreateDefault(), new WindowsTrayLocalCommandLauncher())
    {
    }

    internal WindowsTrayApplicationContext(
        TrayProtectionController controller,
        DefaultStorageLayout layout)
        : this(controller, layout, new WindowsTrayLocalCommandLauncher())
    {
    }

    internal WindowsTrayApplicationContext(
        TrayProtectionController controller,
        DefaultStorageLayout layout,
        ITrayLocalCommandLauncher commandLauncher)
        : this(controller, layout, commandLauncher, new MessageBoxTrayProtectionDisableConfirmation())
    {
    }

    internal WindowsTrayApplicationContext(
        TrayProtectionController controller,
        DefaultStorageLayout layout,
        ITrayLocalCommandLauncher commandLauncher,
        ITrayProtectionDisableConfirmation disableConfirmation,
        SingleInstanceEnforcement? singleInstanceEnforcement = null,
        Func<NativeSubmitRuntimeSet?>? nativeSubmitRuntimeFactory = null,
        Func<IFirstRunSetupController>? firstRunSetupControllerFactory = null,
        Action<FirstRunSetupResult?>? firstRunSetupCompleted = null,
        string localProtectionStatus = LocalProtectionRecovery.ReadyCode,
        Func<ResidentProtectionRuntime>? recoveredRuntimeFactory = null,
        Action<Action>? backgroundWorkQueue = null,
        Action<Action>? uiDispatcher = null,
        bool scheduleFirstRunSetup = true,
        Func<LocalProtectionRecoveryResult>? localProtectionRecovery = null,
        Func<bool>? recoveryConfirmation = null,
        Action<string, MessageBoxIcon>? recoveryMessagePresenter = null,
        Func<IReadOnlyList<SubmitBindingProfile>, NativeSubmitRuntimeSet?>? candidateNativeSubmitRuntimeFactory = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _commandLauncher = commandLauncher ?? throw new ArgumentNullException(nameof(commandLauncher));
        _disableConfirmation = disableConfirmation ?? throw new ArgumentNullException(nameof(disableConfirmation));
        _buildVersion = BuildVersion.Current;
        _crashDiagnostics = LocalCrashDiagnostics.Bootstrap();
        _singleInstanceEnforcement = singleInstanceEnforcement;
        _nativeSubmitRuntimeFactory = nativeSubmitRuntimeFactory;
        _candidateNativeSubmitRuntimeFactory = candidateNativeSubmitRuntimeFactory
            ?? (profiles => WindowsTrayApp.CreateNativeSubmitRuntimeSet(
                Sanitizer.CreateProduction(_layout),
                _layout,
                profiles));
        _firstRunSetupControllerFactory = firstRunSetupControllerFactory
            ?? (() => new FirstRunSetupController(_controller.PublishSetupVerificationProgress));
        _firstRunSetupCompleted = firstRunSetupCompleted;
        _recoveredRuntimeFactory = recoveredRuntimeFactory ?? (() =>
            WindowsTrayApp.CreateResidentProtectionRuntime(Sanitizer.CreateProduction(_layout), _layout));
        _localProtectionRecovery = localProtectionRecovery ?? (() => LocalProtectionRecovery.Recover(_layout, confirmed: true));
        _recoveryConfirmation = recoveryConfirmation ?? ConfirmLocalProtectionRepair;
        _recoveryMessagePresenter = recoveryMessagePresenter ?? ShowLocalProtectionRecoveryMessage;
        if (!string.Equals(localProtectionStatus, LocalProtectionRecovery.ReadyCode, StringComparison.Ordinal))
        {
            _controller.PublishLocalProtectionStatus(localProtectionStatus);
        }

        _activationWindow = new Form
        {
            Text = AppStrings.Get("ProductName"),
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            Size = new System.Drawing.Size(1, 1),
            Location = new System.Drawing.Point(-32000, -32000),
            Opacity = 0,
            StartPosition = FormStartPosition.Manual
        };
        _activationWindow.Show();
        SingleInstanceEnforcement.RegisterActivationWindow("tray", _activationWindow.Handle);
        _backgroundWorkQueue = backgroundWorkQueue ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
        _uiDispatcher = uiDispatcher ?? (work => _activationWindow.BeginInvoke(new MethodInvoker(work)));
        _remediationActionExecutor = new TrayRemediationActionExecutor(_backgroundWorkQueue, _uiDispatcher);

        _statusItem = new ToolStripMenuItem("Protection status", null, (_, _) => OpenLocalProtectionStatus());
        _versionItem = new ToolStripMenuItem(TrayMenuContent.FormatBuildVersionMenuItem(_buildVersion)) { Enabled = false };
        _emergencyBypassItem = new ToolStripMenuItem(
            $"Emergency bypass: {NativeSubmitEmergencyState.BypassDisplayText}") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Stop protection", null, (_, _) => ToggleProtection());
        _repairLocalProtectionItem = new ToolStripMenuItem("Repair local protection", null, (_, _) => RepairLocalProtection())
        {
            Visible = string.Equals(
                _controller.State.LocalProtectionStatus,
                LocalProtectionRecovery.RecoveryRequiredCode,
                StringComparison.Ordinal)
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_versionItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(_emergencyBypassItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open protection status", null, (_, _) => OpenLocalProtectionStatus()));
        menu.Items.Add(new ToolStripMenuItem("Open local restore", null, (_, _) => OpenLocalRestore()));
        menu.Items.Add(new ToolStripMenuItem("Open sensitive terms", null, (_, _) => OpenDictionaryManagement()));
        menu.Items.Add(new ToolStripMenuItem("Set up prompt protection", null, (_, _) => VerifyProfilesFromTray()));
        menu.Items.Add(_repairLocalProtectionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };

        _controller.StateChanged += (_, _) => RefreshStatusOnUiThread();
        var started = _controller.Start();
        RefreshStatus();
        if (!started)
        {
            ShowStartupFailure();
        }

        // Begin only after Application.Run starts pumping messages. The native hook
        // has already been registered and must remain dispatchable while setup waits.
        if (scheduleFirstRunSetup)
        {
            ScheduleFirstRunSetupIfRequired();
        }
    }

    private void ScheduleFirstRunSetupIfRequired()
    {
        if (Interlocked.Exchange(ref _firstRunSetupScheduled, 1) != 0)
        {
            return;
        }

        try
        {
            _activationWindow.BeginInvoke(new MethodInvoker(() =>
                ThreadPool.QueueUserWorkItem(_ => RunFirstRunSetupWorker())));
        }
        catch (InvalidOperationException)
        {
            // The application is already closing; the setup gate stays fail-closed.
        }
    }

    private void RunFirstRunSetupWorker()
    {
        var result = FirstRunSetupBackgroundRunner.Run(
            _layout,
            _firstRunSetupControllerFactory,
            exception => LocalCrashDiagnostics.CaptureDefault(exception, "first_run_setup", "setup_failed"));

        if (!_activationWindow.IsDisposed)
        {
            try
            {
                _activationWindow.BeginInvoke(new MethodInvoker(() => CompleteFirstRunSetup(result)));
            }
            catch (InvalidOperationException)
            {
                // The application is already closing; no runtime reload is needed.
            }
        }
    }

    private void CompleteFirstRunSetup(FirstRunSetupResult? result)
    {
        if (result?.Succeeded == true && !result.State.Required)
        {
            var setupAttemptId = SetupVerificationAttemptId(result);
            var requiresAttemptId = result.PendingProfiles is not null;
            if ((requiresAttemptId && setupAttemptId <= 0)
                || (setupAttemptId > 0 && !_controller.IsCurrentSetupVerificationAttempt(setupAttemptId)))
            {
                return;
            }

            _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "activating_protection",
                "wait_for_verification",
                result.Diagnostics.TryGetValue("profile_id", out var profileId) ? profileId : null,
                _controller.State.ProtectedSendBinding,
                SetupVerificationAttemptId(result)));
            NativeSubmitRuntimeSet? candidateRuntimeSet = null;
            try
            {
                candidateRuntimeSet = result.PendingProfiles is { } candidateProfiles
                    ? _candidateNativeSubmitRuntimeFactory(candidateProfiles)
                    : _nativeSubmitRuntimeFactory?.Invoke();
                if (candidateRuntimeSet is null || !_controller.ReloadNativeSubmit(candidateRuntimeSet))
                {
                    candidateRuntimeSet?.HookHost.Stop();
                    RestorePreviousSetupProfiles(result);
                    _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                        "activation_failed", "restart_protection", AttemptId: SetupVerificationAttemptId(result)));
                    MessageBox.Show(
                        "Setup was verified, but protected Send could not be activated. The existing Send gate remains fail-closed. Open profile verification from the tray to retry.",
                        "Codex Redaction Gate - Setup required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
                else if (result.PendingProfiles is not null && !IsCurrentSetupAttempt(result))
                {
                    if (!ReferenceEquals(
                            _controller.GetCurrentSnapshot().RuntimeSet,
                            candidateRuntimeSet))
                    {
                        candidateRuntimeSet.HookHost.Stop();
                    }

                    // A stale completion cannot roll back a runtime published by
                    // another setup attempt. Keep the current gate active and
                    // require a fresh verification instead.
                    _controller.PublishPromptProtectionRetryFailure();
                    _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                        "activation_failed", "restart_protection", AttemptId: SetupVerificationAttemptId(result)));
                }
                else if (CommitActivatedProfiles(result, result.PendingProfiles))
                {
                    _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                        "protected",
                        "none",
                        result.Diagnostics.TryGetValue("profile_id", out var activatedProfileId) ? activatedProfileId : null,
                        _controller.State.ProtectedSendBinding,
                        SetupVerificationAttemptId(result)));
                }
            }
            catch (Exception exception)
            {
                LocalCrashDiagnostics.CaptureDefault(exception, "first_run_setup", "runtime_reload_failed");
                var candidatePublished = candidateRuntimeSet is not null
                    && ReferenceEquals(_controller.GetCurrentSnapshot().RuntimeSet, candidateRuntimeSet);
                if (candidatePublished)
                {
                    RollbackActivatedSetup(result);
                }
                else
                {
                    candidateRuntimeSet?.HookHost.Stop();
                    RestorePreviousSetupProfiles(result);
                }
                _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                    "activation_failed", "restart_protection", AttemptId: SetupVerificationAttemptId(result)));
                MessageBox.Show(
                    "Setup was verified, but protected Send could not be activated. The existing Send gate remains fail-closed. Open profile verification from the tray to retry.",
                    "Codex Redaction Gate - Setup required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        else if (result is null || result.Code != "setup_cancelled")
        {
            _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                result?.Code == "focused_surface_unverified" ? "unsupported_surface" : "verification_failed",
                "retry_setup", AttemptId: SetupVerificationAttemptId(result)));
            MessageBox.Show(
                "Setup could not be completed. Protected Send remains blocked until verification succeeds. Open profile verification from the tray to retry.",
                "Codex Redaction Gate - Setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        else
        {
            _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "setup_cancelled", "retry_setup", AttemptId: SetupVerificationAttemptId(result)));
        }

        RefreshStatus();
        try
        {
            _firstRunSetupCompleted?.Invoke(result);
        }
        catch (Exception exception)
        {
            _crashDiagnostics.Capture(exception, "first_run_setup", "completion_callback_failed");
        }
    }

    private bool CommitActivatedProfiles(
        FirstRunSetupResult result,
        IReadOnlyList<SubmitBindingProfile>? pendingProfiles)
    {
        try
        {
            if (pendingProfiles is not null && !IsCurrentSetupAttempt(result))
            {
                _controller.PublishPromptProtectionRetryFailure();
                return false;
            }

            if (pendingProfiles is not null)
            {
                var saveResult = SubmitBindingProfileStore.Save(_layout, pendingProfiles);
                if (!saveResult.Succeeded)
                {
                    throw new InvalidOperationException("profile_commit_failed");
                }
            }

            FirstRunSetupController.MarkSetupComplete(_layout);
            return true;
        }
        catch (Exception exception)
        {
            RollbackActivatedSetup(result);
            _crashDiagnostics.Capture(exception, "first_run_setup", "profile_commit_failed");
            _controller.PublishSetupVerificationProgress(new PromptProtectionSetupProgress(
                "activation_failed", "restart_protection", AttemptId: SetupVerificationAttemptId(result)));
            return false;
        }
    }

    private void RollbackActivatedSetup(FirstRunSetupResult result)
    {
        var profilesRestored = RestorePreviousSetupProfiles(result);

        NativeSubmitRuntimeSet? rollbackRuntime = null;
        try
        {
            rollbackRuntime = _nativeSubmitRuntimeFactory?.Invoke();
        }
        catch (Exception exception)
        {
            _crashDiagnostics.Capture(exception, "first_run_setup", "runtime_rollback_create_failed");
        }

        var rollbackSucceeded = profilesRestored
            && rollbackRuntime is not null
            && _controller.ReloadNativeSubmit(rollbackRuntime);
        if (!rollbackSucceeded)
        {
            // Keep the currently published guarded runtime in place. Stopping it here
            // would remove the selected-app gate and could allow the original Send
            // through while setup recovery is unresolved.
            _controller.PublishPromptProtectionRetryFailure();
        }
    }

    private static long SetupVerificationAttemptId(FirstRunSetupResult? result)
    {
        return result?.Diagnostics.TryGetValue("setup_attempt_id", out var text) == true
            && long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    private bool IsCurrentSetupAttempt(FirstRunSetupResult result)
    {
        var attemptId = SetupVerificationAttemptId(result);
        return attemptId > 0 && _controller.IsCurrentSetupVerificationAttempt(attemptId);
    }

    private bool RestorePreviousSetupProfiles(FirstRunSetupResult result)
    {
        if (result.PreviousProfiles is not { } previousProfiles)
        {
            return true;
        }

        try
        {
            var saveResult = SubmitBindingProfileStore.Save(_layout, previousProfiles);
            if (!saveResult.Succeeded)
            {
                _crashDiagnostics.Capture(
                    new InvalidOperationException("profile_rollback_failed"),
                    "first_run_setup",
                    "profile_rollback_failed");
            }

            return saveResult.Succeeded;
        }
        catch (Exception exception)
        {
            _crashDiagnostics.Capture(exception, "first_run_setup", "profile_rollback_failed");
            return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopProtectionAndHideIcon();
            SingleInstanceEnforcement.ClearActivationWindow("tray");
            _activationWindow.Dispose();
            _localProtectionStatusForm?.Dispose();
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ToggleProtection()
    {
        if (_controller.State.Enabled)
        {
            if (!_disableConfirmation.Confirm("stop protection", _controller.State))
            {
                return;
            }

            var result = _controller.TryDisableProtection("stop_protection", confirmed: true);
            if (!result.Succeeded)
            {
                ShowDisableRejected(result);
            }
        }
        else
        {
            _controller.Start();
        }
    }

    internal void RefreshStatus()
    {
        var state = _controller.State;
        var localProtectionStatus = LocalProtectionRecovery.ToSafeStatusCode(state.LocalProtectionStatus);
        _versionItem.Text = TrayMenuContent.FormatBuildVersionMenuItem(_buildVersion);
        if (!string.Equals(localProtectionStatus, LocalProtectionRecovery.ReadyCode, StringComparison.Ordinal))
        {
            _notifyIcon.Text = TrayStatusFormatter.FormatRecoveryRequiredNotifyIconText(localProtectionStatus);
            _statusItem.Text = "Protection needs repair: select Repair local protection";
        }
        else
        {
            _notifyIcon.Text = TrayStatusFormatter.FormatNotifyIconText(state, _buildVersion);
            _statusItem.Text = FormatReadableProtectionStatus(state);
        }

        _toggleItem.Text = state.Enabled ? "Stop protection" : "Start protection";
        _repairLocalProtectionItem.Visible = string.Equals(
            localProtectionStatus,
            LocalProtectionRecovery.RecoveryRequiredCode,
            StringComparison.Ordinal)
            || string.Equals(
                localProtectionStatus,
                LocalProtectionRecovery.RuntimeDegradedCode,
                StringComparison.Ordinal);
        _localProtectionStatusForm?.RefreshView();
    }

    internal static string FormatReadableProtectionStatus(TrayProtectionState state)
    {
        if (!state.Enabled)
        {
            return "Prompt protection: stopped";
        }

        if (state.LastProtectedSendInterruption is not null)
        {
            return "Protected Send: previous Send was interrupted; retry protection before sending";
        }

        if (!string.Equals(state.LocalProtectionStatus, LocalProtectionRecovery.ReadyCode, StringComparison.Ordinal))
        {
            return "Prompt protection: local protection needs repair before sending";
        }

        if (state.SetupVerificationStatus != "idle"
            && (state.SetupVerificationStatus != "protected"
                || (state.ProtectedSendAttemptStatus == "idle" && state.ComposerProtected)))
        {
            return FormatSetupVerificationStatus(state);
        }

        if (state.SetupRequired)
        {
            return $"Prompt setup required for {ProfileDisplayName(state)}: select Set up prompt protection";
        }

        if (state.LastStatus == OsInteractionStatusIds.EmergencyDisabled)
        {
            return $"Emergency bypass is active: {NativeSubmitEmergencyState.BypassDisplayText}";
        }

        if (state.LastProtectedSendInterruption is not null)
        {
            return "Protected Send: previous Send was interrupted; retry protection before sending";
        }

        switch (state.ProtectedSendAttemptStatus)
        {
            case "detected":
            case "checking":
                return "Protected Send: checking prompt";
            case "in_progress":
                return "Protected Send: previous send is still in progress";
            case "sent_safely":
                return "Protected Send: sent safely";
            case "composer_changed":
                return "Protected Send: focus the original composer and send again";
            case "binding_not_verified":
                return "Protected Send: verify prompt protection before sending";
            case "setup_required":
                return "Protected Send: set up prompt protection before sending";
            case "canceled":
                return "Protected Send: canceled; edit the prompt or send again";
            case "local_protection_unavailable":
                return "Protected Send: local protection is unavailable; repair local protection before sending";
            case "policy_blocked":
                return "Protected Send: blocked by policy; contact the administrator";
            case "protection_unavailable":
                return "Protected Send: protection is unavailable; retry protection before sending";
            case "content_blocked":
                return "Protected Send: edit the prompt and send again";
            case "trace_unavailable":
                return "Protected Send: trace unavailable; retry protection before sending";
            case "settings_unavailable":
                return "Protected Send: profile settings unavailable; repair profile settings before sending";
        }

        if (state.ComposerProtected)
        {
            return $"Prompt protection: {ProfileDisplayName(state)} protected, Send {state.ProtectedSendBinding}";
        }

        return state.ReadinessStatus switch
        {
            OsInteractionStatusIds.BindingUnknown or OsInteractionStatusIds.NotConfigured
                => $"Prompt setup incomplete for {ProfileDisplayName(state)}: no verified Send key is saved; select Set up prompt protection",
            OsInteractionStatusIds.SurfaceUnverified or OsInteractionStatusIds.NotComposer
                => $"Prompt verification required for {ProfileDisplayName(state)}: select Set up prompt protection",
            OsInteractionStatusIds.FocusLost or OsInteractionStatusIds.StaleComposer
                => "Prompt was not sent because its original composer changed: focus it and send again",
            OsInteractionStatusIds.DegradedHotkeyOnly
                => "Prompt protection is unavailable: restart protection, then select Set up prompt protection",
            OsInteractionStatusIds.ProfilesUnavailable
                => "Prompt protection is unavailable: repair profile settings before sending",
            _ => "Prompt protection is unavailable: select Set up prompt protection"
        };
    }

    private static string ProfileDisplayName(TrayProtectionState state)
    {
        var profileId = state.SetupVerificationProfileId ?? state.LastProfileId ?? state.ConfiguredProfileId;
        return profileId switch
        {
            "chatgpt-desktop" => "ChatGPT Desktop",
            "codex-desktop" => "Codex Desktop",
            _ => "the selected desktop app"
        };
    }

    private static string FormatSetupVerificationStatus(TrayProtectionState state)
    {
        return state.SetupVerificationStatus switch
        {
            "waiting_for_focus" => "Prompt setup: focus the message composer in the selected app",
            "composer_recognized" => $"Prompt setup: {ProfileDisplayName(state)} composer recognized",
            "verifying_binding" => $"Prompt setup: verifying {PromptProtectionSetupLifecycle.SafeBinding(state.SetupVerificationBinding)}",
            "activating_protection" => "Prompt setup: activating protected Send",
            "protected" => $"Prompt setup: {ProfileDisplayName(state)} is protected, Send {state.SetupVerificationBinding}",
            "unsupported_surface" => "Prompt setup: the focused window was not a supported composer; focus it and try again",
            "activation_failed" => "Prompt setup: verification succeeded, but protected Send could not start; restart protection",
            "setup_cancelled" => "Prompt setup: not completed; select Set up prompt protection",
            _ => "Prompt setup: verification failed; focus the message composer and try again"
        };
    }

    private void RefreshStatusOnUiThread()
    {
        if (_activationWindow.IsDisposed)
        {
            return;
        }

        if (!_activationWindow.InvokeRequired)
        {
            RefreshStatusSafely();
            return;
        }

        try
        {
            _uiDispatcher(RefreshStatusSafely);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            // A closing dispatcher cannot render status; the resident snapshot remains authoritative.
        }
    }

    private void RefreshStatusSafely()
    {
        try
        {
            RefreshStatus();
        }
        catch (Exception exception)
        {
            _crashDiagnostics.Capture(exception, "tray_status", "status_refresh_failed");
        }
    }

    internal void OpenLocalProtectionStatus()
    {
        RefreshProjectFileProtectionStatus();
        if (_localProtectionStatusForm is { IsDisposed: false })
        {
            _localProtectionStatusForm.Activate();
            return;
        }

        _localProtectionStatusForm = new LocalProtectionStatusForm(
            CreateLocalProtectionStatusView,
            RunLocalProtectionStatusAction);
        _localProtectionStatusForm.FormClosed += (_, _) => _localProtectionStatusForm = null;
        _localProtectionStatusForm.Show();
    }

    internal void RefreshProjectFileProtectionStatus()
    {
        _controller.RefreshProjectFileProtectionStatus();
    }

    private LocalProtectionStatusView CreateLocalProtectionStatusView()
    {
        var state = _controller.State;
        return LocalProtectionStatusView.Create(state);
    }

    internal void RunLocalProtectionStatusAction(LocalProtectionStatusAction action)
    {
        switch (action)
        {
            case LocalProtectionStatusAction.VerifyProfiles:
                VerifyProfilesFromTray();
                break;
            case LocalProtectionStatusAction.RetryPromptProtection:
                RetryPromptProtectionFromTray();
                break;
            case LocalProtectionStatusAction.RepairLocalProtection:
                RepairLocalProtection();
                break;
            case LocalProtectionStatusAction.RepairProfileSettings:
                OpenProfileSettings();
                break;
        }
    }

    private void OpenProfileSettings()
    {
        try
        {
            _layout.EnsureDirectories();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_layout.SettingsDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _crashDiagnostics.Capture(exception, "tray_profile_settings", "open_failed");
            _recoveryMessagePresenter(
                "Profile settings could not be opened. Protected Send remains blocked.",
                MessageBoxIcon.Error);
        }
    }

    private void VerifyProfilesFromTray()
    {
        _remediationActionExecutor.TryRun(
            () =>
            {
                var setupController = _firstRunSetupControllerFactory();
                try
                {
                    return setupController is IFocusedProfileSetupController focusedSetupController
                        ? focusedSetupController.ConfigureFocusedProfile(_layout)
                        : setupController.EnsureSetup(_layout);
                }
                catch (Exception exception)
                {
                    _crashDiagnostics.Capture(exception, "tray_profile_verification", "verification_failed");
                    return new FirstRunSetupResult(
                        Succeeded: false,
                        Code: "setup_failed",
                        State: new FirstRunSetupState(true, new[] { "focused_supported_app" }, "failed", false, false),
                        Diagnostics: new Dictionary<string, string>());
                }
            },
            CompleteFirstRunSetup,
            exception => PublishRemediationFailure(exception, "tray_profile_verification", "worker_failed"),
            _ => PublishPromptProtectionRetryFailure());
    }

    private void RetryPromptProtectionFromTray()
    {
        _remediationActionExecutor.TryRun(
            CreatePromptProtectionRetryRuntime,
            CompletePromptProtectionRetry,
            exception => PublishRemediationFailure(exception, "tray_prompt_protection_retry", "worker_failed"),
            runtimeSet =>
            {
                StopUnactivatedRuntime(runtimeSet);
                PublishPromptProtectionRetryFailure();
            });
    }

    private NativeSubmitRuntimeSet? CreatePromptProtectionRetryRuntime()
    {
        try
        {
            return _nativeSubmitRuntimeFactory?.Invoke();
        }
        catch (Exception exception)
        {
            _crashDiagnostics.Capture(exception, "tray_prompt_protection_retry", "runtime_create_failed");
            return null;
        }
    }

    private void CompletePromptProtectionRetry(NativeSubmitRuntimeSet? runtimeSet)
    {
        var retrySucceeded = false;
        try
        {
            retrySucceeded = runtimeSet is not null && _controller.ReloadNativeSubmit(runtimeSet);
        }
        catch (Exception exception)
        {
            StopUnactivatedRuntime(runtimeSet);
            _crashDiagnostics.Capture(exception, "tray_prompt_protection_retry", "runtime_activate_failed");
        }

        if (!retrySucceeded)
        {
            _controller.PublishPromptProtectionRetryFailure();
        }

        RefreshStatus();
    }

    private static void StopUnactivatedRuntime(NativeSubmitRuntimeSet? runtimeSet)
    {
        if (runtimeSet is null)
        {
            return;
        }

        runtimeSet.HookHost.Stop();
        runtimeSet.Dispose();
    }

    private void PublishRemediationFailure(Exception exception, string component, string code)
    {
        _crashDiagnostics.Capture(exception, component, code);
        PublishPromptProtectionRetryFailure();
    }

    private void PublishPromptProtectionRetryFailure()
    {
        _controller.PublishPromptProtectionRetryFailure();
    }

    private void RepairLocalProtection()
    {
        if (!_recoveryConfirmation())
        {
            return;
        }

        RepairLocalProtectionConfirmed();
    }

    internal void RepairLocalProtectionConfirmed()
    {
        _controller.PublishLocalProtectionStatus(LocalProtectionRecovery.ReloadingCode);
        var localRecoveryCompleted = false;
        try
        {
            var result = _localProtectionRecovery();
            if (!result.Succeeded)
            {
                _controller.PublishLocalProtectionStatus(LocalProtectionRecovery.RecoveryRequiredCode);
                _recoveryMessagePresenter(
                    "Local protection repair could not be completed. Protected Send remains blocked.",
                    MessageBoxIcon.Error);
                return;
            }

            localRecoveryCompleted = true;
            var runtime = _recoveredRuntimeFactory();
            if (runtime.NativeSubmitRuntimeSet is null
                || !_controller.ReloadResidentRuntime(runtime)
                || (!_controller.State.Enabled && !_controller.Start())
                || !_controller.TryPublishLocalProtectionReady())
            {
                _controller.PublishLocalProtectionStatus(LocalProtectionRecovery.RuntimeDegradedCode);
                _recoveryMessagePresenter(
                    "Local protection was repaired, but protected Send could not be reactivated. It remains blocked.",
                    MessageBoxIcon.Warning);
                return;
            }

            _nativeSubmitRuntimeFactory = () =>
                _recoveredRuntimeFactory().NativeSubmitRuntimeSet;
            var protectedSendActive = _controller.State.NativeSubmitEnabled
                && _controller.State.ComposerProtected;
            _recoveryMessagePresenter(
                protectedSendActive
                    ? "Local protection was repaired and protected Send is active again."
                    : "Local protection was repaired. Protected Send remains blocked until profile verification succeeds.",
                protectedSendActive ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            _crashDiagnostics.Capture(exception, "local_protection_recovery", "runtime_reload_failed");
            _controller.PublishLocalProtectionStatus(localRecoveryCompleted
                ? LocalProtectionRecovery.RuntimeDegradedCode
                : LocalProtectionRecovery.RecoveryRequiredCode);
            _recoveryMessagePresenter(
                "Local protection was repaired, but protected Send could not be reactivated. It remains blocked.",
                MessageBoxIcon.Warning);
        }
    }

    private static bool ConfirmLocalProtectionRepair()
    {
        return MessageBox.Show(
            "Local protection cannot open the previous encrypted mappings. Repair creates a new protected local state; old restorable placeholders may no longer be recoverable. Continue?",
            "Codex Redaction Gate - Repair local protection",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    private static void ShowLocalProtectionRecoveryMessage(string message, MessageBoxIcon icon)
    {
        MessageBox.Show(
            message,
            "Codex Redaction Gate - Repair local protection",
            MessageBoxButtons.OK,
            icon);
    }

    private void OpenCommand(TrayLocalCommand command)
    {
        try
        {
            _commandLauncher.Open(command);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _crashDiagnostics.Capture(exception, "tray_command", "command_failed");
            MessageBox.Show(
                PublicFailureText.Format(exception, "Command"),
                "Codex Redaction Gate - Command failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OpenLocalRestore()
    {
        try
        {
            using var form = new LocalRestoreForm(LocalRestoreWorkflow.CreateProduction(_layout));
            form.ShowDialog();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _crashDiagnostics.Capture(exception, "local_restore", "local_restore_failed");
            MessageBox.Show(
                PublicFailureText.Format(exception, "Local restore"),
                "Codex Redaction Gate - Local restore failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OpenDictionaryManagement()
    {
        try
        {
            using var form = new DictionaryManagementForm(_layout);
            form.ShowDialog();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _crashDiagnostics.Capture(exception, "dictionary_management", "dictionary_management_failed");
            MessageBox.Show(
                PublicFailureText.Format(exception, "Sensitive terms"),
                "Codex Redaction Gate - Sensitive terms failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void ShowLocalText(string title, string text)
    {
        MessageBox.Show(text, $"Codex Redaction Gate - {title}", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowStartupFailure()
    {
        _notifyIcon.BalloonTipTitle = "Codex Redaction Gate - Protection disabled";
        _notifyIcon.BalloonTipText = TrayStatusFormatter.FormatStartupError(_controller.State);
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void Exit()
    {
        if (_controller.State.Enabled
            && !_disableConfirmation.Confirm("exit Code Sanitizer", _controller.State))
        {
            return;
        }

        var result = _controller.TryDisableProtection("exit", confirmed: true);
        if (!result.Succeeded)
        {
            ShowDisableRejected(result);
            return;
        }

        _notifyIcon.Visible = false;
        _singleInstanceEnforcement?.Dispose();
        ExitThread();
    }

    private void StopProtectionAndHideIcon()
    {
        _controller.Stop();
        _notifyIcon.Visible = false;
    }

    private static void ShowDisableRejected(ProtectionDisableResult result)
    {
        MessageBox.Show(
            $"Protection is still running. status={result.Code}",
            "Code Sanitizer - Protection remains active",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}

internal interface ITrayProtectionDisableConfirmation
{
    bool Confirm(string action, TrayProtectionState state);
}

internal sealed class MessageBoxTrayProtectionDisableConfirmation : ITrayProtectionDisableConfirmation
{
    public bool Confirm(string action, TrayProtectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(state);

        return MessageBox.Show(
            TrayProtectionDisableConfirmationText.Format(action, state),
            "Code Sanitizer - Disable protection?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}

internal static class TrayProtectionDisableConfirmationText
{
    public static string Format(string action, TrayProtectionState state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(state);

        return string.Join(
            Environment.NewLine,
            $"Confirm {action}.",
            "Selected AI apps will no longer be protected while Code Sanitizer is stopped.",
            $"protected_send_binding={state.ProtectedSendBinding}",
            $"readiness={state.ReadinessStatus}");
    }
}

internal static class FirstRunSetupBackgroundRunner
{
    public static FirstRunSetupResult? Run(
        DefaultStorageLayout layout,
        Func<IFirstRunSetupController> controllerFactory,
        Action<Exception> captureFailure)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(controllerFactory);
        ArgumentNullException.ThrowIfNull(captureFailure);

        try
        {
            return new FirstRunSetupLaunchCoordinator(layout, controllerFactory()).RunIfRequired();
        }
        catch (Exception exception)
        {
            captureFailure(exception);
            return null;
        }
    }
}

internal interface ITrayLocalCommandLauncher
{
    void Open(TrayLocalCommand command);
}

internal sealed class WindowsTrayLocalCommandLauncher : ITrayLocalCommandLauncher
{
    public void Open(TrayLocalCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Process.Start(CreateStartInfo(command, AppContext.BaseDirectory, Environment.ProcessPath));
    }

    internal static ProcessStartInfo CreateStartInfo(
        TrayLocalCommand command,
        string appBaseDirectory,
        string? currentProcessPath)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        var consoleExePath = Path.Combine(appBaseDirectory, "CodexRedactionGate.exe");
        var consoleDllPath = Path.Combine(appBaseDirectory, "CodexRedactionGate.dll");
        var invocation = File.Exists(consoleExePath)
            ? $"& {QuotePowerShell(consoleExePath)} {command.CliArgument}"
            : File.Exists(consoleDllPath)
                ? $"& {QuotePowerShell(ResolveDotnetPath(currentProcessPath))} {QuotePowerShell(consoleDllPath)} {command.CliArgument}"
                : throw new InvalidOperationException("Codex Redaction Gate console command target was not found.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(invocation);
        return startInfo;
    }

    private static string ResolveDotnetPath(string? currentProcessPath)
    {
        return !string.IsNullOrWhiteSpace(currentProcessPath)
            && string.Equals(Path.GetFileName(currentProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            ? currentProcessPath
            : "dotnet";
    }

    private static string QuotePowerShell(string value)
    {
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}

internal sealed class WindowsTrayHotkeyHost : NativeWindow, ITrayHotkeyHost
{
    private const int HotkeyId = 0x5248;
    private const int WmHotkey = 0x0312;

    private readonly HotkeyDefinition _hotkey;
    private Action? _onTriggered;
    private bool _started;

    public WindowsTrayHotkeyHost()
        : this(HotkeySettingsStore.DefaultProtectionHotkey)
    {
    }

    public WindowsTrayHotkeyHost(HotkeyDefinition hotkey)
    {
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
    }

    public HotkeyBinding Binding => _hotkey.Binding;

    public string? LastErrorCode { get; private set; }

    public bool Start(Action onTriggered)
    {
        ArgumentNullException.ThrowIfNull(onTriggered);

        if (_started)
        {
            _onTriggered = onTriggered;
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            LastErrorCode = OsInteractionStatusIds.UnsupportedPlatform;
            return false;
        }

        CreateHandle(new CreateParams());
        if (!NativeMethods.RegisterHotKey(Handle, HotkeyId, _hotkey.Modifiers, _hotkey.VirtualKey))
        {
            LastErrorCode = $"hotkey_register_failed:{Marshal.GetLastPInvokeError()}";
            DestroyHandle();
            return false;
        }

        _onTriggered = onTriggered;
        _started = true;
        LastErrorCode = null;
        return true;
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
        _started = false;
        _onTriggered = null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
        {
            _onTriggered?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

}
