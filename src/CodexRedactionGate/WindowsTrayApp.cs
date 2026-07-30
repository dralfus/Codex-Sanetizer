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
            localProtectionStatus: LocalProtectionRecovery.RecoveryRequiredCode);
    }

    internal static int Run(
        ISanitizer sanitizer,
        DefaultStorageLayout layout,
        bool useGlobalMutex,
        Action<WindowsTrayApplicationContext> runMessageLoop,
        SingleInstanceNotificationSettings? secondInstanceNotificationSettings,
        Action<SecondInstanceNotification>? secondInstanceNotificationPresenter = null,
        string localProtectionStatus = LocalProtectionRecovery.ReadyCode)
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

        // Single instance enforcement - second launch should activate existing instance and exit
        if (SingleInstanceEnforcement.IsAnotherInstanceRunning("tray", useGlobalMutex))
        {
            var activationSucceeded = SingleInstanceEnforcement.ActivateExistingInstance("tray", useGlobalMutex);
            NotifyBlockedSecondInstance(
                secondInstanceNotificationSettings,
                activationSucceeded,
                secondInstanceNotificationPresenter);
            return 0; // Exit cleanly - existing instance will handle everything
        }

        using var enforcement = new SingleInstanceEnforcement("tray", useGlobalMutex);
        if (!enforcement.IsFirstInstance)
        {
            var activationSucceeded = SingleInstanceEnforcement.ActivateExistingInstance("tray", useGlobalMutex);
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
            localProtectionStatus: localProtectionStatus);
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
        ShowAlreadyRunningNotification(settings, activationSucceeded, presenter);
    }

    private static void ShowAlreadyRunningNotification(
        SingleInstanceNotificationSettings settings,
        bool activationSucceeded,
        Action<SecondInstanceNotification>? presenter)
    {
        var notificationDetails = CreateSecondInstanceNotification(settings, activationSucceeded);
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
        bool activationSucceeded)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled || settings.Type == "none")
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
        var runtimeSet = CreateNativeSubmitRuntimeSet(sanitizer, resolvedLayout);
        var runtime = runtimeSet?.Runtimes.FirstOrDefault();
        var nativeProfile = runtime?.Profile;
        var liveAdapter = new WindowsVerifiedComposerSurfaceAdapter();
        var activeSurfaceDiscovery = WindowsFocusedComposerDiscovery.CreateDefault();
        var orchestrator = new OsInteractionOrchestrator(
            sanitizer,
            WindowsFocusedComposerDiscovery.CreateDefault(),
            liveAdapter,
            liveAdapter,
            liveAdapter,
            new WindowsConfirmationOverlay());

        return new TrayProtectionController(
            settings.Usable
                ? new WindowsTrayHotkeyHost(settings.Settings.ProtectionHotkey)
                : new UnavailableTrayHotkeyHost(settings.Settings.ProtectionHotkey.Binding, settings.Code),
            () => orchestrator.RunOnce(OsInteractionRunOptions.ApplyOnly),
            runtime?.HookHost,
            runtime?.Controller,
            runtime?.Runner,
            nativeProfile,
            storageLayout: resolvedLayout,
            sendControlDiscovery: WindowsSendControlDiscovery.CreateDefault(resolvedLayout),
            nativeSubmitRuntimes: runtimeSet?.Runtimes,
            activeSurfaceDiscovery: activeSurfaceDiscovery.DiscoverActiveSurface);
    }

    internal static NativeSubmitRuntime? CreateNativeSubmitRuntime(ISanitizer sanitizer, DefaultStorageLayout layout)
    {
        return CreateNativeSubmitRuntimeSet(sanitizer, layout)?.Runtimes.FirstOrDefault();
    }

    internal static NativeSubmitRuntimeSet? CreateNativeSubmitRuntimeSet(ISanitizer sanitizer, DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);

        var profiles = ResolveNativeProfilesForProtection(layout);
        if (profiles.Count == 0)
        {
            return null;
        }

        var hookHost = new WindowsNativeSubmitHookHost();
        var activeSurfaceDiscovery = WindowsFocusedComposerDiscovery.CreateDefault();
        var runtimes = profiles.Select(nativeProfile =>
        {
            var controller = new NativeSubmitInterceptionController(
                nativeProfile,
                new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
                activeSurfaceDiscovery: activeSurfaceDiscovery.DiscoverActiveSurface,
                firstRunSetupController: new FirstRunSetupController(),
                setupLayout: layout);
            OsInteractionResult RunConfirmAndSend(NativeSubmitTargetIdentity? target)
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
                    new WindowsConfirmationOverlay());
                return nativeSubmitOrchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);
            }

            return new NativeSubmitRuntime(
                hookHost,
                controller,
                () => RunConfirmAndSend(target: null),
                nativeProfile,
                target => RunConfirmAndSend(target));
        }).ToArray();
        return new NativeSubmitRuntimeSet(hookHost, runtimes);
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
    private readonly SingleInstanceEnforcement? _singleInstanceEnforcement;
    private readonly Func<NativeSubmitRuntimeSet?>? _nativeSubmitRuntimeFactory;
    private readonly Func<IFirstRunSetupController> _firstRunSetupControllerFactory;
    private readonly Action<FirstRunSetupResult?>? _firstRunSetupCompleted;
    private readonly string _localProtectionStatus;
    private LocalProtectionStatusForm? _localProtectionStatusForm;
    private int _firstRunSetupScheduled;
    private int _profileVerificationInProgress;

    internal bool IsTrayIconVisible => _notifyIcon.Visible;

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
        string localProtectionStatus = LocalProtectionRecovery.ReadyCode)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _commandLauncher = commandLauncher ?? throw new ArgumentNullException(nameof(commandLauncher));
        _disableConfirmation = disableConfirmation ?? throw new ArgumentNullException(nameof(disableConfirmation));
        _buildVersion = BuildVersion.Current;
        _crashDiagnostics = LocalCrashDiagnostics.Bootstrap();
        _singleInstanceEnforcement = singleInstanceEnforcement;
        _nativeSubmitRuntimeFactory = nativeSubmitRuntimeFactory;
        _firstRunSetupControllerFactory = firstRunSetupControllerFactory ?? (() => new FirstRunSetupController());
        _firstRunSetupCompleted = firstRunSetupCompleted;
        _localProtectionStatus = localProtectionStatus;

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

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _versionItem = new ToolStripMenuItem(TrayMenuContent.FormatBuildVersionMenuItem(_buildVersion)) { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Stop protection", null, (_, _) => ToggleProtection());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_versionItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open local protection status", null, (_, _) => OpenLocalProtectionStatus()));
        menu.Items.Add(new ToolStripMenuItem("Open local restore", null, (_, _) => OpenLocalRestore()));
        menu.Items.Add(new ToolStripMenuItem("Open sensitive terms", null, (_, _) => OpenDictionaryManagement()));
        menu.Items.Add(new ToolStripMenuItem("Verify Codex Desktop profile", null, (_, _) => OpenCommand(TrayMenuContent.VerifyCodexProfileCommand)));
        menu.Items.Add(new ToolStripMenuItem("Verify ChatGPT Desktop profile", null, (_, _) => OpenCommand(TrayMenuContent.VerifyChatGptProfileCommand)));
        menu.Items.Add(new ToolStripMenuItem("Open audit viewer", null, (_, _) => OpenCommand(TrayMenuContent.AuditViewerCommand)));
        menu.Items.Add(new ToolStripMenuItem("Open diagnostics", null, (_, _) => OpenCommand(TrayMenuContent.DiagnosticsCommand)));
        if (string.Equals(_localProtectionStatus, LocalProtectionRecovery.RecoveryRequiredCode, StringComparison.Ordinal))
        {
            menu.Items.Add(new ToolStripMenuItem("Repair local protection", null, (_, _) => RepairLocalProtection()));
        }
        menu.Items.Add(new ToolStripMenuItem("Command reference...", null, (_, _) => ShowLocalText("Commands", TrayMenuContent.FormatBuildVersionHelpText(_buildVersion) + Environment.NewLine + Environment.NewLine + TrayMenuContent.RestoreText + Environment.NewLine + Environment.NewLine + TrayMenuContent.DiagnosticsText + Environment.NewLine + Environment.NewLine + TrayMenuContent.RuleManagementText)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true
        };

        _controller.StateChanged += (_, _) => RefreshStatus();
        var started = _controller.Start();
        RefreshStatus();
        if (!started)
        {
            ShowStartupFailure();
        }

        // Begin only after Application.Run starts pumping messages. The native hook
        // has already been registered and must remain dispatchable while setup waits.
        ScheduleFirstRunSetupIfRequired();
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
            try
            {
                var runtimeSet = _nativeSubmitRuntimeFactory?.Invoke();
                if (runtimeSet is null || !_controller.ReloadNativeSubmit(runtimeSet))
                {
                    MessageBox.Show(
                        "Setup was verified, but protected Send could not be activated. The existing Send gate remains fail-closed. Open profile verification from the tray to retry.",
                        "Codex Redaction Gate - Setup required",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
            catch (Exception exception)
            {
                LocalCrashDiagnostics.CaptureDefault(exception, "first_run_setup", "runtime_reload_failed");
                MessageBox.Show(
                    "Setup was verified, but protected Send could not be activated. The existing Send gate remains fail-closed. Open profile verification from the tray to retry.",
                    "Codex Redaction Gate - Setup required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        else if (result is null || result.Code != "setup_cancelled")
        {
            MessageBox.Show(
                "Setup could not be completed. Protected Send remains blocked until verification succeeds. Open profile verification from the tray to retry.",
                "Codex Redaction Gate - Setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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

    private void RefreshStatus()
    {
        _versionItem.Text = TrayMenuContent.FormatBuildVersionMenuItem(_buildVersion);
        if (string.Equals(_localProtectionStatus, LocalProtectionRecovery.RecoveryRequiredCode, StringComparison.Ordinal))
        {
            _notifyIcon.Text = TrayStatusFormatter.FormatRecoveryRequiredNotifyIconText(_localProtectionStatus);
            _statusItem.Text = $"local_protection={_localProtectionStatus} protected_send=blocked repair_required=true";
        }
        else
        {
            _notifyIcon.Text = TrayStatusFormatter.FormatNotifyIconText(_controller.State, _buildVersion);
            _statusItem.Text = $"local_protection={_localProtectionStatus} {TrayStatusFormatter.FormatMenuStatus(_controller.State)}";
        }

        _toggleItem.Text = _controller.State.Enabled ? "Stop protection" : "Start protection";
        _localProtectionStatusForm?.RefreshView();
    }

    private void OpenLocalProtectionStatus()
    {
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

    private LocalProtectionStatusView CreateLocalProtectionStatusView()
    {
        var inspection = LocalProtectionRecovery.Inspect(_layout);
        var localProtectionStatus = inspection.Succeeded
            ? LocalProtectionRecovery.ReadyCode
            : inspection.Code;
        var state = _controller.State;
        return LocalProtectionStatusView.Create(
            localProtectionStatus,
            state,
            ProjectFileProtectionStatusInspector.Inspect(_layout));
    }

    private void RunLocalProtectionStatusAction(LocalProtectionStatusAction action)
    {
        switch (action)
        {
            case LocalProtectionStatusAction.VerifyProfiles:
                VerifyProfilesFromTray();
                break;
            case LocalProtectionStatusAction.RepairLocalProtection:
                RepairLocalProtection();
                break;
        }
    }

    private void VerifyProfilesFromTray()
    {
        if (Interlocked.Exchange(ref _profileVerificationInProgress, 1) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var result = FirstRunSetupBackgroundRunner.Run(
                _layout,
                _firstRunSetupControllerFactory,
                exception => _crashDiagnostics.Capture(exception, "tray_profile_verification", "verification_failed"));
            try
            {
                _activationWindow.BeginInvoke(new MethodInvoker(() =>
                {
                    try
                    {
                        CompleteFirstRunSetup(result);
                    }
                    finally
                    {
                        Volatile.Write(ref _profileVerificationInProgress, 0);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                Volatile.Write(ref _profileVerificationInProgress, 0);
            }
        });
    }

    private void RepairLocalProtection()
    {
        var confirmed = MessageBox.Show(
            "Local protection cannot open the previous encrypted mappings. Repair creates a new protected local state; old restorable placeholders may no longer be recoverable. Continue?",
            "Codex Redaction Gate - Repair local protection",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;
        if (!confirmed)
        {
            return;
        }

        var result = LocalProtectionRecovery.Recover(_layout, confirmed: true);
        if (!result.Succeeded)
        {
            MessageBox.Show(
                $"Local protection repair could not be completed. status={result.Code}",
                "Codex Redaction Gate - Repair local protection",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        MessageBox.Show(
            "Local protection was repaired. Code Sanitizer will restart before protected Send is re-enabled.",
            "Codex Redaction Gate - Repair local protection",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        Application.Restart();
        ExitThread();
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
