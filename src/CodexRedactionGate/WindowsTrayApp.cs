using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexRedactionGate;

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
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);

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
            if (!SingleInstanceEnforcement.ActivateExistingInstance("tray", useGlobalMutex))
            {
                ShowAlreadyRunningNotification(SingleInstanceNotificationSettings.Load());
            }
            return 0; // Exit cleanly - existing instance will handle everything
        }

        using var enforcement = new SingleInstanceEnforcement("tray", useGlobalMutex);
        if (!enforcement.IsFirstInstance)
        {
            SingleInstanceEnforcement.ActivateExistingInstance("tray", useGlobalMutex);
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
            () => CreateNativeSubmitRuntime(sanitizer, layout));
        Application.Run(context);
        return 0;
    }

    private static void ShowAlreadyRunningNotification(SingleInstanceNotificationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled || settings.Type == "none")
        {
            return;
        }

        if (settings.Type == "messagebox")
        {
            MessageBox.Show(
                AppStrings.Get("AlreadyRunning"),
                AppStrings.Get("ProductName"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        using var notification = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            BalloonTipTitle = AppStrings.Get("ProductName"),
            BalloonTipText = AppStrings.Get("AlreadyRunning")
        };
        notification.ShowBalloonTip(3000);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(50);
        }
    }

    internal static TrayProtectionController CreateController(ISanitizer sanitizer, DefaultStorageLayout? layout = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);

        var settings = HotkeySettingsStore.Load(layout ?? DefaultStorageLayout.CreateDefault());
        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        var runtime = CreateNativeSubmitRuntime(sanitizer, resolvedLayout);
        var nativeProfile = runtime?.Profile;
        var liveAdapter = new WindowsVerifiedComposerSurfaceAdapter();
        var activeSurfaceDiscovery = WindowsActiveSurfaceDiscovery.CreateDefault();
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
            sendControlDiscovery: WindowsSendControlDiscovery.CreateDefault());
    }

    internal static NativeSubmitRuntime? CreateNativeSubmitRuntime(ISanitizer sanitizer, DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(layout);

        var nativeProfile = ResolveNativeProfileForProtection(layout);
        if (nativeProfile is null)
        {
            return null;
        }

        var activeSurfaceDiscovery = WindowsActiveSurfaceDiscovery.CreateDefault();
        var controller = new NativeSubmitInterceptionController(
            nativeProfile,
            new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5)),
            activeSurfaceDiscovery: activeSurfaceDiscovery.DiscoverActiveSurface,
            firstRunSetupController: new FirstRunSetupController(),
            setupLayout: layout);
        return new NativeSubmitRuntime(
            new WindowsNativeSubmitHookHost(),
            controller,
            () =>
            {
                var nativeSubmitAdapter = new WindowsVerifiedComposerSurfaceAdapter();
                var nativeSubmitOrchestrator = new OsInteractionOrchestrator(
                    sanitizer,
                    WindowsFocusedComposerDiscovery.CreateDefault(),
                    nativeSubmitAdapter,
                    nativeSubmitAdapter,
                    new VerifiedSubmitBindingAction(nativeSubmitAdapter, nativeProfile),
                    new WindowsConfirmationOverlay());
                return nativeSubmitOrchestrator.RunOnce(OsInteractionRunOptions.ConfirmAndSend);
            },
            nativeProfile);
    }

    internal static SubmitBindingProfile? ResolveNativeProfileForProtection(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var profiles = SubmitBindingProfileStore.Load(layout).Profiles;
        // Prefer setup-complete profiles, then protected profiles, then any profile
        return profiles.FirstOrDefault(profile => profile.IsSetupComplete)
            ?? profiles.FirstOrDefault(profile => profile.IsProtected)
            ?? profiles.FirstOrDefault()
            ?? FirstRunSetupController.CreateDefaultSetupProfile("codex-desktop");
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
    private readonly Func<NativeSubmitRuntime?>? _nativeSubmitRuntimeFactory;

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
        Func<NativeSubmitRuntime?>? nativeSubmitRuntimeFactory = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _commandLauncher = commandLauncher ?? throw new ArgumentNullException(nameof(commandLauncher));
        _disableConfirmation = disableConfirmation ?? throw new ArgumentNullException(nameof(disableConfirmation));
        _buildVersion = BuildVersion.Current;
        _crashDiagnostics = new LocalCrashDiagnostics(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexRedactionGate",
            "crashes"));
        _singleInstanceEnforcement = singleInstanceEnforcement;
        _nativeSubmitRuntimeFactory = nativeSubmitRuntimeFactory;

        _activationWindow = new Form
        {
            Text = SingleInstanceEnforcement.ActivationWindowTitle,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            Size = new System.Drawing.Size(1, 1),
            Location = new System.Drawing.Point(-32000, -32000),
            Opacity = 0,
            StartPosition = FormStartPosition.Manual
        };
        _activationWindow.Show();

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _versionItem = new ToolStripMenuItem(TrayMenuContent.FormatBuildVersionMenuItem(_buildVersion)) { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Stop protection", null, (_, _) => ToggleProtection());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_versionItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open local restore", null, (_, _) => OpenLocalRestore()));
        menu.Items.Add(new ToolStripMenuItem("Open sensitive terms", null, (_, _) => OpenDictionaryManagement()));
        menu.Items.Add(new ToolStripMenuItem("Verify Codex Desktop profile", null, (_, _) => OpenCommand(TrayMenuContent.VerifyCodexProfileCommand)));
        menu.Items.Add(new ToolStripMenuItem("Verify ChatGPT Desktop profile", null, (_, _) => OpenCommand(TrayMenuContent.VerifyChatGptProfileCommand)));
        menu.Items.Add(new ToolStripMenuItem("Open audit viewer", null, (_, _) => OpenCommand(TrayMenuContent.AuditViewerCommand)));
        menu.Items.Add(new ToolStripMenuItem("Open diagnostics", null, (_, _) => OpenCommand(TrayMenuContent.DiagnosticsCommand)));
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

        // Launch first-run setup if no protected profile exists (ticket 232)
        LaunchFirstRunSetupIfRequired();
    }

    private void LaunchFirstRunSetupIfRequired()
    {
        try
        {
            // Check if no protected profiles exist
            var profilesResult = SubmitBindingProfileStore.Load(_layout);
            var hasProtectedProfile = profilesResult.Profiles.Any(p => p.IsProtected && p.Enabled);
            var hasSetupComplete = profilesResult.Profiles.Any(p => p.IsSetupComplete);

            if (!hasSetupComplete)
            {
                // Launch first-run setup
                var setupController = new FirstRunSetupController();
                var setupResult = setupController.EnsureSetup(_layout);

                // Only refresh status if setup succeeded and profile is setup complete
                if (setupResult.Succeeded)
                {
                    // Wait for setup completion and verify profile is setup complete
                    var finalStatus = setupController.GetSetupStatus(_layout);
                    if (!finalStatus.State.Required)
                    {
                        var runtime = _nativeSubmitRuntimeFactory?.Invoke();
                        if (runtime is not null)
                        {
                            _controller.ReloadNativeSubmit(runtime);
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            LocalCrashDiagnostics.CaptureDefault(exception, "first_run_setup", "setup_failed");
            MessageBox.Show(
                "Setup could not be completed. Protected Send remains blocked until verification succeeds.",
                "Codex Redaction Gate - Setup required",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopProtectionAndHideIcon();
            _activationWindow.Dispose();
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
        _notifyIcon.Text = TrayStatusFormatter.FormatNotifyIconText(_controller.State, _buildVersion);
        _versionItem.Text = TrayMenuContent.FormatBuildVersionMenuItem(_buildVersion);
        _statusItem.Text = TrayStatusFormatter.FormatMenuStatus(_controller.State);
        _toggleItem.Text = _controller.State.Enabled ? "Stop protection" : "Start protection";
    }

    private void OpenCommand(TrayLocalCommand command)
    {
        try
        {
            _commandLauncher.Open(command);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                exception.Message,
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
            MessageBox.Show(
                exception.Message,
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
            MessageBox.Show(
                exception.Message,
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
