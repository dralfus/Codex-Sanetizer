using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record ProductSmokeReport(
    bool Passed,
    bool InstallArtifactPresent,
    bool ResidentTrayLaunchPassed,
    bool AutostartResidentCommandPassed,
    bool FirstRunPassed,
    bool HotkeyRegistrationPassed,
    bool ProtectedTriggerStatusPassed,
    bool UnloadConfirmationPassed,
    bool ComposerProtectionStatusPassed,
    bool ProjectFileProtectionStatusPassed,
    bool ProjectFileReadOnlySmokePassed,
    bool ProjectFileProductSmokePassed,
    bool DictionaryPolicySetupPassed,
    bool SampleSanitizePassed,
    bool DisposableApplyOnlyPassed,
    bool AuditViewPassed,
    bool RestorePassed,
    bool UninstallSafePassed,
    bool NativeSubmitInterceptionPassed,
    bool NativeSubmitRepeatabilityPassed,
    bool NativeSubmitDuplicateGuardPassed,
    bool NativeSubmitOverlayForegroundRequestPassed,
    bool NativeSubmitOverlayForegroundRefusalStatusPassed,
    bool NativeProfileVerificationEntrypointsPassed,
    bool SetupEnforcementRegressionPassed,
    bool RawFreeArtifactsPassed,
    int AuditRowCount,
    int SanitizedPlaceholderCount,
    string SupportedTargetStatement);

public static class ProductSmokeRunner
{
    public const string SupportedTargetStatement = "windows_codex_chatgpt_desktop_only";

    public static ProductSmokeReport RunInstalledArtifactSmoke(
        string appSourceDirectory,
        string installDirectory,
        DefaultStorageLayout layout,
        byte[] hmacSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appSourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(hmacSecret);

        CopyInstalledArtifact(appSourceDirectory, installDirectory);
        var appArtifactPath = ResolveInstalledArtifactPath(installDirectory);

        return Run(layout, appArtifactPath, hmacSecret);
    }

    private static string ResolveInstalledArtifactPath(string installDirectory)
    {
        foreach (var fileName in new[]
        {
            "CodexRedactionGate.Tray.exe",
            "CodexRedactionGate.exe",
            "CodexRedactionGate.dll"
        })
        {
            var candidate = Path.Combine(installDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(installDirectory, "CodexRedactionGate.Tray.exe");
    }

    public static ProductSmokeReport Run(DefaultStorageLayout layout, string appArtifactPath, byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(appArtifactPath);
        ArgumentNullException.ThrowIfNull(hmacSecret);

        var installArtifactPresent = File.Exists(appArtifactPath);
        var installDirectory = Path.GetDirectoryName(Path.GetFullPath(appArtifactPath)) ?? string.Empty;
        var residentTrayExecutable = Path.Combine(installDirectory, "CodexRedactionGate.Tray.exe");
        var residentTrayLaunchPassed = File.Exists(residentTrayExecutable)
            || ReleasePackagingDeclaresResidentTray();
        var autostartResidentCommandPassed = ReleasePackagingDeclaresResidentTray();
        layout.EnsureDirectories();
        var firstRunPassed = Directory.Exists(layout.PolicyDirectory)
            && Directory.Exists(layout.VaultDirectory)
            && Directory.Exists(layout.AuditDirectory)
            && Directory.Exists(layout.SettingsDirectory);

        var hotkey = HotkeySettingsStore.SaveProtectionHotkey(layout, "Ctrl+Shift+F9");
        var loadedHotkey = HotkeySettingsStore.Load(layout);
        var hotkeyPassed = hotkey.Succeeded
            && loadedHotkey.Usable
            && loadedHotkey.Settings.ProtectionHotkey.Binding.DisplayText == "Ctrl+Shift+F9";

        var smokeProfile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(new TextSurfaceDescriptor(
                "product-smoke-native-profile",
                "codex-desktop",
                "Codex desktop smoke composer",
                Supported: true,
                CanCaptureText: true,
                CanReplaceText: true,
                CanSubmit: true,
                Metadata: new SurfaceMetadata(
                    SurfaceKind: "disposable_local_target",
                    CloudSubmission: "false"))));
        var profileSave = SubmitBindingProfileStore.Upsert(layout, smokeProfile);
        var protectedTriggerStatus = TrayStatusFormatter.FormatMenuStatus(new TrayProtectionState(
            Enabled: true,
            Mode: "NativeSubmit",
            Hotkey: loadedHotkey.Settings.ProtectionHotkey.Binding.DisplayText,
            LastStatus: OsInteractionStatusIds.Protected,
            LastDecision: null,
            LastReplacementCount: null,
            LastProfileId: smokeProfile.ProfileId,
            LastApplied: false,
            LastSubmitted: false,
            NativeSubmitEnabled: true,
            NativeSubmitStatus: OsInteractionStatusIds.Protected,
            ProtectedSendBinding: smokeProfile.SubmitBinding!.DisplayText,
            NewlineBinding: smokeProfile.NewlineBinding!.DisplayText,
            ManualScanHotkey: loadedHotkey.Settings.ProtectionHotkey.Binding.DisplayText,
            ReadinessStatus: OsInteractionStatusIds.Protected,
            ComposerProtected: true,
            ProjectFilesProtected: false,
            ProjectFileStatus: ProjectFileProtectionStatusValues.NotConfigured,
            ResidentProcess: true));
        var protectedTriggerStatusPassed = profileSave.Succeeded
            && smokeProfile.IsProtected
            && protectedTriggerStatus.Contains("protected_send_binding=Enter", StringComparison.Ordinal)
            && protectedTriggerStatus.Contains("newline_binding=Ctrl+Enter", StringComparison.Ordinal)
            && protectedTriggerStatus.Contains("manual_scan_hotkey=Ctrl+Shift+F9", StringComparison.Ordinal);
        var composerProtectionStatusPassed = protectedTriggerStatus.Contains("composer_protected=true", StringComparison.Ordinal);
        var projectFileProtectionStatusPassed = protectedTriggerStatus.Contains("project_files_protected=false", StringComparison.Ordinal)
            && protectedTriggerStatus.Contains($"project_file_status={ProjectFileProtectionStatusValues.NotConfigured}", StringComparison.Ordinal);

        var unloadController = new TrayProtectionController(
            new ProductSmokeTrayHotkeyHost("Ctrl+Shift+F9"),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Applied,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: false,
                Submitted: false,
                Diagnostics: new Dictionary<string, string>()),
            new ProductSmokeNativeSubmitHookHost(),
            new NativeSubmitInterceptionController(smokeProfile, new NativeSubmitEmergencyState(TimeSpan.FromMinutes(5))),
            () => new OsInteractionResult(
                OsInteractionStatusIds.Submitted,
                Surface: null,
                SanitizationResult: null,
                ConfirmationModel: null,
                Applied: true,
                Submitted: true,
                Diagnostics: new Dictionary<string, string>()),
            smokeProfile);
        unloadController.Start();
        var canceledUnload = unloadController.TryDisableProtection("exit", confirmed: false);
        var confirmedUnload = unloadController.TryDisableProtection("exit", confirmed: true);
        var unloadConfirmationPassed = !canceledUnload.Succeeded
            && canceledUnload.ProtectionStillRunning
            && confirmedUnload.Succeeded
            && !confirmedUnload.ProtectionStillRunning
            && confirmedUnload.Diagnostics.TryGetValue("raw_prompt_recorded", out var rawPromptRecorded)
            && rawPromptRecorded == "false";

        var dictionary = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout));
        var dictionaryResult = dictionary.Add("customer", "Product Smoke Customer", null);
        var policyResult = new ManagedPolicyRules(layout.PolicyDirectory)
            .AddUrlPrefix("https://product-smoke.example.local/");
        var dictionaryPolicyPassed = dictionaryResult.Succeeded && policyResult.Succeeded;

        var vault = new InMemoryHmacMappingVault(hmacSecret);
        var sanitizer = new Sanitizer(
            vault,
            dictionary.LoadTerms(),
            Sanitizer.LoadProductionPolicy(layout).ActivePolicy,
            secretScanner: null,
            auditSink: new FileAuditSink(layout.AuditDirectory));
        var sample = sanitizer.Sanitize(CreateRequest("Connect to 192.168.10.25 for Product Smoke Customer"));
        var samplePassed = sample.Decision == SanitizeDecision.Confirm
            && sample.Replacements.Count >= 2
            && !sample.SanitizedText.Contains("192.168.10.25", StringComparison.Ordinal)
            && !sample.SanitizedText.Contains("Product Smoke Customer", StringComparison.Ordinal);

        var osSmoke = OsAdapterDemoRunner.RunSmoke(hmacSecret);
        var disposableApplyOnlyPassed = osSmoke.DryRunPassed && osSmoke.ApplyOnlyPassed;

        var auditView = AuditViewer.Load(layout.AuditDirectory);
        var renderedAudit = string.Join(Environment.NewLine, AuditViewer.Render(auditView));
        var auditViewPassed = auditView.Chain.Valid && auditView.Rows.Count > 0;

        var restore = new LocalRestoreWorkflow(new LocalRestorer(vault), new FileAuditSink(layout.AuditDirectory))
            .RestoreText(sample.SanitizedText);
        var restorePassed = restore.Restoration.Metadata.LocalSensitive
            && restore.Restoration.Metadata.RestoredPseudonymCountsByType.Values.Sum() >= 1;

        var cleanupPlan = LocalDataCleanup.Plan(layout);
        var uninstallSafePassed = cleanupPlan.Succeeded
            && !cleanupPlan.Deleted
            && Directory.Exists(layout.VaultDirectory);

        var nativeSubmit = NativeSubmitProductSmokeRunner.Run(hmacSecret);
        var codexVerification = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(CreateSmokeNativeSubmitSurface("codex-desktop")));
        var chatGptVerification = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "chatgpt-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(CreateSmokeNativeSubmitSurface("chatgpt-desktop")));
        var mismatchVerification = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            "codex-desktop",
            "Enter",
            "Ctrl+Enter",
            TextSurfaceDiscoveryResult.Success(CreateSmokeNativeSubmitSurface("chatgpt-desktop")));
        var nativeProfileVerificationEntrypointsPassed = nativeSubmit.Passed
            && codexVerification.IsProtected
            && chatGptVerification.IsProtected
            && mismatchVerification.CapabilityStatus == OsInteractionStatusIds.SurfaceUnverified
            && TrayMenuContent.VerifyCodexProfileCommand.CliArgument.Contains("--native-profile-verify-delay codex-desktop", StringComparison.Ordinal)
            && TrayMenuContent.VerifyChatGptProfileCommand.CliArgument.Contains("--native-profile-verify-delay chatgpt-desktop", StringComparison.Ordinal)
            && TrayMenuContent.RuleManagementText.Contains("--native-profile-verify-delay codex-desktop", StringComparison.Ordinal)
            && TrayMenuContent.RuleManagementText.Contains("--native-profile-verify-delay chatgpt-desktop", StringComparison.Ordinal);
        var projectFileReadOnlySmoke = ProjectFileReadOnlySmokeRunner.Run(hmacSecret);
        var projectFileProductSmoke = ProjectFileProductSmokeRunner.Run(hmacSecret);

        var rawFreeArtifacts = renderedAudit
            + Environment.NewLine
            + string.Join(Environment.NewLine, NativeSubmitProductSmokeRunner.RenderRawFree(nativeSubmit))
            + Environment.NewLine
            + string.Join(Environment.NewLine, ProjectFileReadOnlySmokeRunner.RenderRawFree(projectFileReadOnlySmoke))
            + Environment.NewLine
            + string.Join(Environment.NewLine, ProjectFileProductSmokeRunner.RenderRawFree(projectFileProductSmoke))
            + Environment.NewLine
            + RenderRawFree(new ProductSmokeReport(
                Passed: false,
                InstallArtifactPresent: installArtifactPresent,
                ResidentTrayLaunchPassed: residentTrayLaunchPassed,
                AutostartResidentCommandPassed: autostartResidentCommandPassed,
                FirstRunPassed: firstRunPassed,
                HotkeyRegistrationPassed: hotkeyPassed,
                ProtectedTriggerStatusPassed: protectedTriggerStatusPassed,
                UnloadConfirmationPassed: unloadConfirmationPassed,
                ComposerProtectionStatusPassed: composerProtectionStatusPassed,
                ProjectFileProtectionStatusPassed: projectFileProtectionStatusPassed,
                ProjectFileReadOnlySmokePassed: projectFileReadOnlySmoke.Passed,
                ProjectFileProductSmokePassed: projectFileProductSmoke.Passed,
                DictionaryPolicySetupPassed: dictionaryPolicyPassed,
                SampleSanitizePassed: samplePassed,
                DisposableApplyOnlyPassed: disposableApplyOnlyPassed,
                AuditViewPassed: auditViewPassed,
                RestorePassed: restorePassed,
                UninstallSafePassed: uninstallSafePassed,
                NativeSubmitInterceptionPassed: nativeSubmit.Passed,
                NativeSubmitRepeatabilityPassed: nativeSubmit.RepeatedSubmitPassed,
                NativeSubmitDuplicateGuardPassed: nativeSubmit.DuplicateSendGuardPassed,
                NativeSubmitOverlayForegroundRequestPassed: nativeSubmit.OverlayForegroundRequestPassed,
                NativeSubmitOverlayForegroundRefusalStatusPassed: nativeSubmit.OverlayForegroundRefusalStatusPassed,
                NativeProfileVerificationEntrypointsPassed: nativeProfileVerificationEntrypointsPassed,
                SetupEnforcementRegressionPassed: nativeSubmit.SetupEnforcementRegressionPassed,
                RawFreeArtifactsPassed: false,
                AuditRowCount: auditView.Rows.Count,
                SanitizedPlaceholderCount: sample.Replacements.Count,
                SupportedTargetStatement: SupportedTargetStatement));
        var rawFreePassed = !rawFreeArtifacts.Contains("192.168.10.25", StringComparison.Ordinal)
            && !rawFreeArtifacts.Contains("Product Smoke Customer", StringComparison.Ordinal)
            && !rawFreeArtifacts.Contains("product-smoke.example.local", StringComparison.Ordinal);

        var passed = installArtifactPresent
            && firstRunPassed
            && hotkeyPassed
            && dictionaryPolicyPassed
            && samplePassed
            && disposableApplyOnlyPassed
            && auditViewPassed
            && restorePassed
            && uninstallSafePassed
            && nativeSubmit.Passed
            && residentTrayLaunchPassed
            && autostartResidentCommandPassed
            && protectedTriggerStatusPassed
            && unloadConfirmationPassed
            && composerProtectionStatusPassed
            && projectFileProtectionStatusPassed
            && projectFileReadOnlySmoke.Passed
            && projectFileProductSmoke.Passed
            && nativeSubmit.RepeatedSubmitPassed
            && nativeSubmit.DuplicateSendGuardPassed
            && nativeSubmit.OverlayForegroundRequestPassed
            && nativeSubmit.OverlayForegroundRefusalStatusPassed
            && nativeSubmit.SetupEnforcementRegressionPassed
            && nativeProfileVerificationEntrypointsPassed
            && rawFreePassed;

        return new ProductSmokeReport(
            Passed: passed,
            InstallArtifactPresent: installArtifactPresent,
            ResidentTrayLaunchPassed: residentTrayLaunchPassed,
            AutostartResidentCommandPassed: autostartResidentCommandPassed,
            FirstRunPassed: firstRunPassed,
            HotkeyRegistrationPassed: hotkeyPassed,
            ProtectedTriggerStatusPassed: protectedTriggerStatusPassed,
            UnloadConfirmationPassed: unloadConfirmationPassed,
            ComposerProtectionStatusPassed: composerProtectionStatusPassed,
            ProjectFileProtectionStatusPassed: projectFileProtectionStatusPassed,
            ProjectFileReadOnlySmokePassed: projectFileReadOnlySmoke.Passed,
            ProjectFileProductSmokePassed: projectFileProductSmoke.Passed,
            DictionaryPolicySetupPassed: dictionaryPolicyPassed,
            SampleSanitizePassed: samplePassed,
            DisposableApplyOnlyPassed: disposableApplyOnlyPassed,
            AuditViewPassed: auditViewPassed,
            RestorePassed: restorePassed,
            UninstallSafePassed: uninstallSafePassed,
            NativeSubmitInterceptionPassed: nativeSubmit.Passed,
            NativeSubmitRepeatabilityPassed: nativeSubmit.RepeatedSubmitPassed,
            NativeSubmitDuplicateGuardPassed: nativeSubmit.DuplicateSendGuardPassed,
            NativeSubmitOverlayForegroundRequestPassed: nativeSubmit.OverlayForegroundRequestPassed,
            NativeSubmitOverlayForegroundRefusalStatusPassed: nativeSubmit.OverlayForegroundRefusalStatusPassed,
            NativeProfileVerificationEntrypointsPassed: nativeProfileVerificationEntrypointsPassed,
            SetupEnforcementRegressionPassed: nativeSubmit.SetupEnforcementRegressionPassed,
            RawFreeArtifactsPassed: rawFreePassed,
            AuditRowCount: auditView.Rows.Count,
            SanitizedPlaceholderCount: sample.Replacements.Count,
            SupportedTargetStatement: SupportedTargetStatement);
    }

    private static void CopyInstalledArtifact(string sourceDirectory, string installDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(installDirectory);
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static IReadOnlyList<string> RenderRawFree(ProductSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new[]
        {
            $"status: {(report.Passed ? "product_smoke_passed" : "product_smoke_failed")}",
            $"supported_targets: {report.SupportedTargetStatement}",
            "live_compatibility_note: use_disposable_local_target_first_then_throwaway_codex_or_chatgpt_desktop_task",
            $"install_artifact_present: {report.InstallArtifactPresent.ToString().ToLowerInvariant()}",
            $"resident_tray_launch: {report.ResidentTrayLaunchPassed.ToString().ToLowerInvariant()}",
            $"autostart_resident_command: {report.AutostartResidentCommandPassed.ToString().ToLowerInvariant()}",
            $"first_run: {report.FirstRunPassed.ToString().ToLowerInvariant()}",
            $"hotkey_registration: {report.HotkeyRegistrationPassed.ToString().ToLowerInvariant()}",
            $"protected_trigger_status: {report.ProtectedTriggerStatusPassed.ToString().ToLowerInvariant()}",
            $"unload_confirmation: {report.UnloadConfirmationPassed.ToString().ToLowerInvariant()}",
            $"composer_protected_status: {report.ComposerProtectionStatusPassed.ToString().ToLowerInvariant()}",
            $"project_files_protected_status: {report.ProjectFileProtectionStatusPassed.ToString().ToLowerInvariant()}",
            $"project_file_read_only_smoke: {report.ProjectFileReadOnlySmokePassed.ToString().ToLowerInvariant()}",
            $"project_file_product_smoke: {report.ProjectFileProductSmokePassed.ToString().ToLowerInvariant()}",
            $"dictionary_policy_setup: {report.DictionaryPolicySetupPassed.ToString().ToLowerInvariant()}",
            $"sample_sanitize: {report.SampleSanitizePassed.ToString().ToLowerInvariant()}",
            $"apply_only_write_back: {report.DisposableApplyOnlyPassed.ToString().ToLowerInvariant()}",
            $"audit_view: {report.AuditViewPassed.ToString().ToLowerInvariant()}",
            $"restore: {report.RestorePassed.ToString().ToLowerInvariant()}",
            $"uninstall_safe_default: {report.UninstallSafePassed.ToString().ToLowerInvariant()}",
            $"native_submit_interception: {report.NativeSubmitInterceptionPassed.ToString().ToLowerInvariant()}",
            $"native_submit_repeatability: {report.NativeSubmitRepeatabilityPassed.ToString().ToLowerInvariant()}",
            $"native_submit_duplicate_guard: {report.NativeSubmitDuplicateGuardPassed.ToString().ToLowerInvariant()}",
            $"native_submit_overlay_foreground_request: {report.NativeSubmitOverlayForegroundRequestPassed.ToString().ToLowerInvariant()}",
            $"native_submit_overlay_foreground_refusal_status: {report.NativeSubmitOverlayForegroundRefusalStatusPassed.ToString().ToLowerInvariant()}",
            $"native_profile_verification_entrypoints: {report.NativeProfileVerificationEntrypointsPassed.ToString().ToLowerInvariant()}",
            $"setup_enforcement_regression: {report.SetupEnforcementRegressionPassed.ToString().ToLowerInvariant()}",
            $"raw_free_artifacts: {report.RawFreeArtifactsPassed.ToString().ToLowerInvariant()}",
            $"audit_rows: {report.AuditRowCount}",
            $"sanitized_placeholder_count: {report.SanitizedPlaceholderCount}"
        };
    }

    private static bool ReleasePackagingDeclaresResidentTray()
    {
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var installScript = Path.Combine(repositoryRoot, "scripts", "install-user.ps1");
        var manifest = Path.Combine(repositoryRoot, "packaging", "windows", "CodexRedactionGate.iss");
        var buildScript = Path.Combine(repositoryRoot, "scripts", "build-release.ps1");
        if (!File.Exists(installScript) || !File.Exists(manifest) || !File.Exists(buildScript))
        {
            return false;
        }

        var scriptText = File.ReadAllText(installScript);
        var manifestText = File.ReadAllText(manifest);
        var buildText = File.ReadAllText(buildScript);
        return scriptText.Contains("CodexRedactionGate.Tray.exe", StringComparison.Ordinal)
            && scriptText.Contains("Start-Process -FilePath $trayExe", StringComparison.Ordinal)
            && manifestText.Contains("CodexRedactionGate.Tray.exe", StringComparison.Ordinal)
            && manifestText.Contains("Software\\Microsoft\\Windows\\CurrentVersion\\Run", StringComparison.Ordinal)
            && buildText.Contains("CodexRedactionGate.Tray.csproj", StringComparison.Ordinal);
    }

    private static TextSurfaceDescriptor CreateSmokeNativeSubmitSurface(string profileId)
    {
        return new TextSurfaceDescriptor(
            $"product-smoke-native-profile:{profileId}",
            profileId,
            profileId,
            Supported: true,
            CanCaptureText: true,
            CanReplaceText: true,
            CanSubmit: true,
            Metadata: new SurfaceMetadata(
                SurfaceKind: "disposable_local_target",
                CloudSubmission: "false"));
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "scripts"))
                && Directory.Exists(Path.Combine(directory.FullName, "packaging")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(startDirectory);
    }

    private sealed class ProductSmokeTrayHotkeyHost : ITrayHotkeyHost
    {
        public ProductSmokeTrayHotkeyHost(string displayText)
        {
            Binding = new HotkeyBinding("manual-scan-apply", displayText, "manual_scan_apply");
        }

        public HotkeyBinding Binding { get; }

        public string? LastErrorCode { get; private set; }

        public bool Start(Action onTriggered)
        {
            ArgumentNullException.ThrowIfNull(onTriggered);
            LastErrorCode = null;
            return true;
        }

        public void Stop()
        {
        }
    }

    private sealed class ProductSmokeNativeSubmitHookHost : INativeSubmitHookHost
    {
        public string? LastErrorCode { get; private set; }

        public bool Start(
            Func<NativeKeyGesture, NativeSubmitInterceptionResult> classify,
            Action<NativeKeyGesture> onSuppressedSubmit)
        {
            ArgumentNullException.ThrowIfNull(classify);
            ArgumentNullException.ThrowIfNull(onSuppressedSubmit);
            LastErrorCode = null;
            return true;
        }

        public void Stop()
        {
        }
    }

    private static SanitizeRequest CreateRequest(string text)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart("prompt", ContentSources.PromptText, text, new Dictionary<string, string>())
            },
            Context: new SanitizationContext("product-smoke", null, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none"));
    }
}
