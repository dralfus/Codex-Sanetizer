using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record ProductSmokeReport(
    bool Passed,
    bool InstallArtifactPresent,
    bool FirstRunPassed,
    bool HotkeyRegistrationPassed,
    bool DictionaryPolicySetupPassed,
    bool SampleSanitizePassed,
    bool DisposableApplyOnlyPassed,
    bool AuditViewPassed,
    bool RestorePassed,
    bool UninstallSafePassed,
    bool NativeSubmitInterceptionPassed,
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
        var appArtifactPath = Path.Combine(installDirectory, "CodexRedactionGate.dll");
        if (!File.Exists(appArtifactPath))
        {
            appArtifactPath = Path.Combine(installDirectory, "CodexRedactionGate.exe");
        }

        return Run(layout, appArtifactPath, hmacSecret);
    }

    public static ProductSmokeReport Run(DefaultStorageLayout layout, string appArtifactPath, byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(appArtifactPath);
        ArgumentNullException.ThrowIfNull(hmacSecret);

        var installArtifactPresent = File.Exists(appArtifactPath);
        layout.EnsureDirectories();
        var firstRunPassed = Directory.Exists(layout.PolicyDirectory)
            && Directory.Exists(layout.VaultDirectory)
            && Directory.Exists(layout.AuditDirectory)
            && Directory.Exists(layout.SettingsDirectory);

        var hotkey = HotkeySettingsStore.SaveProtectionHotkey(layout, "Ctrl+Enter");
        var loadedHotkey = HotkeySettingsStore.Load(layout);
        var hotkeyPassed = hotkey.Succeeded
            && loadedHotkey.Usable
            && loadedHotkey.Settings.ProtectionHotkey.Binding.DisplayText == "Ctrl+Enter";

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

        var rawFreeArtifacts = renderedAudit
            + Environment.NewLine
            + string.Join(Environment.NewLine, NativeSubmitProductSmokeRunner.RenderRawFree(nativeSubmit))
            + Environment.NewLine
            + RenderRawFree(new ProductSmokeReport(
                Passed: false,
                InstallArtifactPresent: installArtifactPresent,
                FirstRunPassed: firstRunPassed,
                HotkeyRegistrationPassed: hotkeyPassed,
                DictionaryPolicySetupPassed: dictionaryPolicyPassed,
                SampleSanitizePassed: samplePassed,
                DisposableApplyOnlyPassed: disposableApplyOnlyPassed,
                AuditViewPassed: auditViewPassed,
                RestorePassed: restorePassed,
                UninstallSafePassed: uninstallSafePassed,
                NativeSubmitInterceptionPassed: nativeSubmit.Passed,
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
            && rawFreePassed;

        return new ProductSmokeReport(
            Passed: passed,
            InstallArtifactPresent: installArtifactPresent,
            FirstRunPassed: firstRunPassed,
            HotkeyRegistrationPassed: hotkeyPassed,
            DictionaryPolicySetupPassed: dictionaryPolicyPassed,
            SampleSanitizePassed: samplePassed,
            DisposableApplyOnlyPassed: disposableApplyOnlyPassed,
            AuditViewPassed: auditViewPassed,
            RestorePassed: restorePassed,
            UninstallSafePassed: uninstallSafePassed,
            NativeSubmitInterceptionPassed: nativeSubmit.Passed,
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
            $"first_run: {report.FirstRunPassed.ToString().ToLowerInvariant()}",
            $"hotkey_registration: {report.HotkeyRegistrationPassed.ToString().ToLowerInvariant()}",
            $"dictionary_policy_setup: {report.DictionaryPolicySetupPassed.ToString().ToLowerInvariant()}",
            $"sample_sanitize: {report.SampleSanitizePassed.ToString().ToLowerInvariant()}",
            $"apply_only_write_back: {report.DisposableApplyOnlyPassed.ToString().ToLowerInvariant()}",
            $"audit_view: {report.AuditViewPassed.ToString().ToLowerInvariant()}",
            $"restore: {report.RestorePassed.ToString().ToLowerInvariant()}",
            $"uninstall_safe_default: {report.UninstallSafePassed.ToString().ToLowerInvariant()}",
            $"native_submit_interception: {report.NativeSubmitInterceptionPassed.ToString().ToLowerInvariant()}",
            $"raw_free_artifacts: {report.RawFreeArtifactsPassed.ToString().ToLowerInvariant()}",
            $"audit_rows: {report.AuditRowCount}",
            $"sanitized_placeholder_count: {report.SanitizedPlaceholderCount}"
        };
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
