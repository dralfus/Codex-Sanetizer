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
    internal const string ProductionInstanceId = "tray";

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
        IExistingTrayInstanceActivator? existingInstanceActivator = null,
        string instanceId = ProductionInstanceId)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(runMessageLoop);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

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
        if (SingleInstanceEnforcement.IsAnotherInstanceRunning(instanceId, useGlobalMutex))
        {
            var activationSucceeded = activator.TryActivate(instanceId, useGlobalMutex);
            NotifyBlockedSecondInstance(
                secondInstanceNotificationSettings,
                activationSucceeded,
                secondInstanceNotificationPresenter);
            return 0; // Exit cleanly - existing instance will handle everything
        }

        using var enforcement = new SingleInstanceEnforcement(instanceId, useGlobalMutex);
        if (!enforcement.IsFirstInstance)
        {
            var activationSucceeded = activator.TryActivate(instanceId, useGlobalMutex);
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
            candidateNativeSubmitRuntimeFactory: profiles => CreateNativeSubmitRuntimeSet(sanitizer, layout, profiles),
            instanceId: instanceId);
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
        IReadOnlyList<SubmitBindingProfile>? profilesOverride = null,
        ISubmitBindingProfileAdapter? profileAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);

        var adapter = profileAdapter ?? LocalSubmitBindingProfileAdapter.Instance;
        var loadedProfiles = profilesOverride is null ? adapter.Load(layout) : null;
        var profiles = profilesOverride ?? ResolveNativeProfilesForProtection(loadedProfiles!.Profiles);
        if (profiles.Count == 0)
        {
            return null;
        }

        var hookHost = new WindowsNativeSubmitHookHost(profiles);
        var activeSurfaceDiscovery = WindowsFocusedComposerDiscovery.CreateDefault();
        var confirmationOverlay = new WindowsConfirmationOverlay();
        var runtimes = profiles.Select(nativeProfile =>
        {
            var profileSnapshot = loadedProfiles is null
                ? NativeSubmitProfileSnapshot.FromProfile(nativeProfile)
                : NativeSubmitProfileSnapshotAdapter.FromLoadResult(loadedProfiles, nativeProfile, layout);
            var controller = new NativeSubmitInterceptionController(
                nativeProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: activeSurfaceDiscovery.DiscoverActiveSurface,
                profileSnapshot: profileSnapshot);
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

    internal static SubmitBindingProfile? ResolveNativeProfileForProtection(
        DefaultStorageLayout layout,
        ISubmitBindingProfileAdapter? profileAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return ResolveNativeProfilesForProtection(layout, profileAdapter).FirstOrDefault();
    }

    internal static IReadOnlyList<SubmitBindingProfile> ResolveNativeProfilesForProtection(
        DefaultStorageLayout layout,
        ISubmitBindingProfileAdapter? profileAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var profiles = (profileAdapter ?? LocalSubmitBindingProfileAdapter.Instance).Load(layout).Profiles;
        return ResolveNativeProfilesForProtection(profiles);
    }

    private static IReadOnlyList<SubmitBindingProfile> ResolveNativeProfilesForProtection(
        IReadOnlyList<SubmitBindingProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        if (profiles.Count == 0)
        {
            return new[]
            {
                FirstRunSetupController.CreateDefaultSetupProfile("codex-desktop")!,
                FirstRunSetupController.CreateDefaultSetupProfile("chatgpt-desktop")!
            };
        }

        return profiles
            .Select(ChatGptDesktopCompatibility.RequirePinnedFingerprint)
            .Where(profile => profile.Enabled || profile.ProfileId is "codex-desktop" or "chatgpt-desktop")
            .ToArray();
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

internal sealed class WindowsTrayApplicationContext : ApplicationContext
{
    private readonly IResidentProtectionRuntime _residentRuntime;
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
    private readonly Action<Action> _uiDispatcher;
    private readonly TrayProtectionIntentDispatcher _intentDispatcher;
    private readonly ResidentProtectionWorkflowCoordinator _residentWorkflowCoordinator;
    private readonly string _instanceId;
    private LocalProtectionStatusForm? _localProtectionStatusForm;

    internal bool IsTrayIconVisible => _notifyIcon.Visible;

    internal string TrayTooltipText => _notifyIcon.Text ?? string.Empty;

    internal string TrayStatusText => _statusItem.Text ?? string.Empty;

    internal string EmergencyBypassMenuText => _emergencyBypassItem.Text ?? string.Empty;

    internal bool IsLocalProtectionStatusOpen => _localProtectionStatusForm is { IsDisposed: false, Visible: true };

    internal LocalProtectionStatusForm? LocalProtectionStatusForm => _localProtectionStatusForm;

    internal bool IsNativeSubmitHookReady => _residentRuntime.Snapshot.HookReady;

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
        Func<IReadOnlyList<SubmitBindingProfile>, NativeSubmitRuntimeSet?>? candidateNativeSubmitRuntimeFactory = null,
        string instanceId = WindowsTrayApp.ProductionInstanceId)
    {
        var workflowRuntime = new ResidentProtectionRuntimeFacade(
            controller ?? throw new ArgumentNullException(nameof(controller)));
        _residentRuntime = workflowRuntime;
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? throw new ArgumentException("Instance ID is required.", nameof(instanceId)) : instanceId;
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
            ?? (() => new FirstRunSetupController(workflowRuntime.PublishSetupVerificationProgress));
        _firstRunSetupCompleted = firstRunSetupCompleted;
        _recoveredRuntimeFactory = recoveredRuntimeFactory ?? (() =>
            WindowsTrayApp.CreateResidentProtectionRuntime(Sanitizer.CreateProduction(_layout), _layout));
        _localProtectionRecovery = localProtectionRecovery ?? (() => LocalProtectionRecovery.Recover(_layout, confirmed: true));
        _recoveryConfirmation = recoveryConfirmation ?? ConfirmLocalProtectionRepair;
        _recoveryMessagePresenter = recoveryMessagePresenter ?? ShowLocalProtectionRecoveryMessage;
        if (!string.Equals(localProtectionStatus, LocalProtectionRecovery.ReadyCode, StringComparison.Ordinal))
        {
            workflowRuntime.PublishLocalProtectionStatus(localProtectionStatus);
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
        SingleInstanceEnforcement.RegisterActivationWindow(_instanceId, _activationWindow.Handle);
        var resolvedBackgroundWorkQueue = backgroundWorkQueue ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
        var resolvedUiDispatcher = uiDispatcher ?? (work => _activationWindow.BeginInvoke(new MethodInvoker(work)));
        _uiDispatcher = resolvedUiDispatcher;
        _residentWorkflowCoordinator = new ResidentProtectionWorkflowCoordinator(
            workflowRuntime,
            _layout,
            _firstRunSetupControllerFactory,
            _candidateNativeSubmitRuntimeFactory,
            () => _nativeSubmitRuntimeFactory?.Invoke(),
            _recoveredRuntimeFactory,
            _localProtectionRecovery,
            resolvedBackgroundWorkQueue,
            resolvedUiDispatcher,
            (exception, component, code) => _crashDiagnostics.Capture(exception, component, code));
        _residentWorkflowCoordinator.SetupCompleted += result =>
        {
            try
            {
                _firstRunSetupCompleted?.Invoke(result);
            }
            catch (Exception exception)
            {
                _crashDiagnostics.Capture(exception, "first_run_setup", "completion_callback_failed");
            }
        };
        _residentWorkflowCoordinator.Notice += (message, isFailure) =>
            _recoveryMessagePresenter(message, isFailure ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        _intentDispatcher = new TrayProtectionIntentDispatcher(
            new Dictionary<TrayProtectionIntent, Action>
            {
                [TrayProtectionIntent.ToggleProtection] = ToggleProtection,
                [TrayProtectionIntent.OpenProtectionStatus] = OpenLocalProtectionStatus,
                [TrayProtectionIntent.OpenLocalRestore] = OpenLocalRestore,
                [TrayProtectionIntent.OpenSensitiveTerms] = OpenDictionaryManagement,
                [TrayProtectionIntent.SetupPromptProtection] = VerifyProfilesFromTray,
                [TrayProtectionIntent.RepairLocalProtection] = RepairLocalProtection,
                [TrayProtectionIntent.Exit] = Exit
            });

        _statusItem = new ToolStripMenuItem("Protection status", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.OpenProtectionStatus));
        _versionItem = new ToolStripMenuItem(TrayMenuContent.FormatBuildVersionMenuItem(_buildVersion)) { Enabled = false };
        _emergencyBypassItem = new ToolStripMenuItem(
            $"Emergency bypass: {NativeSubmitEmergencyState.BypassDisplayText}") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Stop protection", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.ToggleProtection));
        _repairLocalProtectionItem = new ToolStripMenuItem("Repair local protection", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.RepairLocalProtection))
        {
            Visible = string.Equals(
                _residentRuntime.Snapshot.State.LocalProtectionStatus,
                LocalProtectionRecovery.RecoveryRequiredCode,
                StringComparison.Ordinal)
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_versionItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(_emergencyBypassItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open protection status", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.OpenProtectionStatus)));
        menu.Items.Add(new ToolStripMenuItem("Open local restore", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.OpenLocalRestore)));
        menu.Items.Add(new ToolStripMenuItem("Open sensitive terms", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.OpenSensitiveTerms)));
        menu.Items.Add(new ToolStripMenuItem("Set up prompt protection", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.SetupPromptProtection)));
        menu.Items.Add(_repairLocalProtectionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => DispatchTrayIntent(TrayProtectionIntent.Exit)));

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };

        _residentRuntime.SnapshotChanged += (_, _) => RefreshStatusOnUiThread();
        var started = _residentWorkflowCoordinator.StartResident();
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
        _residentWorkflowCoordinator.StartInitialSetup();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopProtectionAndHideIcon();
            SingleInstanceEnforcement.ClearActivationWindow(_instanceId);
            _activationWindow.Dispose();
            _localProtectionStatusForm?.Dispose();
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void DispatchTrayIntent(TrayProtectionIntent intent)
    {
        _intentDispatcher.TryDispatch(intent);
    }

    private void ToggleProtection()
    {
        if (_residentRuntime.Snapshot.State.Enabled)
        {
            if (!_disableConfirmation.Confirm("stop protection", _residentRuntime.Snapshot.State))
            {
                return;
            }

            var result = _residentRuntime.TryDisableProtection("stop_protection", confirmed: true);
            if (!result.Succeeded)
            {
                ShowDisableRejected(result);
            }
        }
        else
        {
            _residentWorkflowCoordinator.StartResident();
        }
    }

    internal void RefreshStatus()
    {
        _residentWorkflowCoordinator.RefreshOperationalState();
        var state = _residentRuntime.Snapshot.State;
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

        if (state.PromptProtectionRetryInProgress)
        {
            return "Protected Send: retrying protection";
        }

        if (state.LastProtectedSendInterruption is not null)
        {
            return "Protected Send: previous Send was interrupted; retry protection before sending";
        }

        if (!string.Equals(state.LocalProtectionStatus, LocalProtectionRecovery.ReadyCode, StringComparison.Ordinal))
        {
            return "Prompt protection: local protection needs repair before sending";
        }

        var operationalAction = state.EffectiveOperationalAction;
        if (string.Equals(operationalAction.ActionKind, "local_readiness", StringComparison.Ordinal)
            && operationalAction.Status == "running")
        {
            return $"Local readiness: stage={operationalAction.Stage}, input={operationalAction.InputMode}, elapsed={operationalAction.ElapsedMilliseconds} ms, next={operationalAction.NextAction}";
        }

        if (string.Equals(operationalAction.ActionKind, "local_readiness", StringComparison.Ordinal)
            && operationalAction.Status is "failed" or "cancelled")
        {
            return "Local readiness: failed; retry the local readiness action before sending";
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

        if (IsChatGptProtectedClaimUnproven(state)
            && state.NativeSubmitEnabled
            && state.ComposerProtected)
        {
            return $"OpenAI Desktop resident Send is protected; release/CI evidence is not current: reference={state.ReferenceAcceptanceStatus}, live={state.LiveContractStatus}";
        }

        if (state.ComposerProtected)
        {
            return $"Prompt protection: {ProfileDisplayName(state)} keyboard Send protected ({state.ProtectedSendBinding}); mouse Send is not protected";
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

    private static bool IsChatGptProtectedClaimUnproven(TrayProtectionState state)
    {
        return string.Equals(state.ConfiguredProfileId, "chatgpt-desktop", StringComparison.Ordinal)
            && !string.Equals(state.ProtectedClaimStatus, OsInteractionStatusIds.Protected, StringComparison.Ordinal)
            && !string.Equals(state.ProtectedClaimStatus, OsInteractionStatusIds.NotConfigured, StringComparison.Ordinal);
    }

    private static string ProfileDisplayName(TrayProtectionState state)
    {
        var profileId = state.SetupVerificationProfileId ?? state.LastProfileId ?? state.ConfiguredProfileId;
        return profileId is "chatgpt-desktop" or "codex-desktop"
            ? "OpenAI Desktop"
            : "the selected desktop app";
    }

    private static string FormatSetupVerificationStatus(TrayProtectionState state)
    {
        var message = state.SetupVerificationStatus switch
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
        var action = state.EffectiveOperationalAction;
        return action.Status == "running"
            ? $"{message}; stage={action.Stage}, input={action.InputMode}, elapsed={action.ElapsedMilliseconds} ms, next={action.NextAction}"
            : message;
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
        _residentRuntime.RefreshDiagnostics();
    }

    private LocalProtectionStatusView CreateLocalProtectionStatusView()
    {
        var state = _residentRuntime.Snapshot.State;
        return LocalProtectionStatusView.Create(state);
    }

    internal void RunLocalProtectionStatusAction(LocalProtectionStatusAction action)
    {
        switch (action)
        {
            case LocalProtectionStatusAction.VerifyProfiles:
                VerifyProfilesFromTray();
                break;
            case LocalProtectionStatusAction.RunLocalReadiness:
                _residentWorkflowCoordinator.StartLocalReadiness();
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
            case LocalProtectionStatusAction.CancelOperationalAction:
                _residentWorkflowCoordinator.CancelCurrentOperation();
                RefreshStatus();
                break;
            case LocalProtectionStatusAction.RetryOperationalAction:
                RetryOperationalActionFromTray();
                break;
        }
    }

    private void RetryOperationalActionFromTray()
    {
        _residentWorkflowCoordinator.RetryCurrentOperation();
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
        _residentWorkflowCoordinator.StartFocusedSetup();
    }

    private void RetryPromptProtectionFromTray()
    {
        _residentWorkflowCoordinator.RetryPromptProtection();
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
        _residentWorkflowCoordinator.RepairLocalProtection();
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
        _notifyIcon.BalloonTipText = TrayStatusFormatter.FormatStartupError(_residentRuntime.Snapshot.State);
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Error;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void Exit()
    {
        if (_residentRuntime.Snapshot.State.Enabled
            && !_disableConfirmation.Confirm("exit Code Sanitizer", _residentRuntime.Snapshot.State))
        {
            return;
        }

        var result = _residentRuntime.TryDisableProtection("exit", confirmed: true);
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
        _residentRuntime.Stop();
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
