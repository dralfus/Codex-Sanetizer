using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace CodexRedactionGate;

public static class Program
{
    internal static Func<TextSurfaceDiscoveryResult> NativeProfileDiscoveryFactory { get; set; } =
        () => WindowsFocusedComposerDiscovery.CreateDefault().DiscoverActiveSurface();

    // Static constructor to register crash handlers
    static Program()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            try
            {
                var crashDiag = new LocalCrashDiagnostics(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CodexRedactionGate",
                        "crashes"));
                crashDiag.Capture((Exception)args.ExceptionObject, "appdomain_unhandled");
            }
            catch
            {
                // Swallow any logging errors to avoid cascading failures
            }
        };

        Application.ThreadException += (sender, args) =>
        {
            try
            {
                var crashDiag = new LocalCrashDiagnostics(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "CodexRedactionGate",
                        "crashes"));
                crashDiag.Capture(args.Exception, "ui_thread");
            }
            catch
            {
                // Swallow any logging errors to avoid cascading failures
            }
        };
    }

    [STAThread]
    public static int Main(string[] args)
    {
        return Main(args, CreateDefaultRuntime());
    }

    internal static int Main(string[] args, Func<Sanitizer> sanitizerFactory)
    {
        return Main(args, _ => sanitizerFactory());
    }

    internal static int Main(string[] args, Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory)
    {
        return Main(args, CreateRuntime(sanitizerFactory, DefaultStorageLayout.CreateDefault));
    }

    internal static int Main(
        string[] args,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory,
        Func<ManagedPolicyLoadResult> policyLoadFactory,
        Func<ManagedPolicyLoadResult, Sanitizer> policyTestSanitizerFactory)
    {
        return Main(
            args,
            new CliRuntime(
                sanitizerFactory,
                policyLoadFactory,
                policyTestSanitizerFactory,
                DefaultStorageLayout.CreateDefault,
                LocalRestoreWorkflow.CreateProduction));
    }

    internal static int Main(
        string[] args,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory,
        Func<ManagedPolicyLoadResult> policyLoadFactory,
        Func<ManagedPolicyLoadResult, Sanitizer> policyTestSanitizerFactory,
        Func<DefaultStorageLayout> layoutFactory)
    {
        return Main(
            args,
            new CliRuntime(
                sanitizerFactory,
                policyLoadFactory,
                policyTestSanitizerFactory,
                layoutFactory,
                LocalRestoreWorkflow.CreateProduction));
    }

    internal static int Main(string[] args, CliRuntime runtime)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            try
            {
                return RunSelfTest(() => runtime.SanitizerFactory(Array.Empty<DictionaryTerm>()));
            }
            catch (Exception exception) when (exception is InvalidOperationException or DpapiSecretLoadFailureException)
            {
                LocalCrashDiagnostics.CaptureDefault(exception, "self_test", "self_test_initialization_failed");
                Console.WriteLine("status: self_test_unavailable");
                Console.WriteLine("failure: local_protection_initialization_failed");
                return 1;
            }
        }

        if (args.Length == 2 && args[0] == "--sanitize")
        {
            return RunSanitize(args[1], Array.Empty<DictionaryTerm>(), runtime.SanitizerFactory);
        }

        if (args.Length == 2 && args[0] == "--restore-text")
        {
            return RunRestoreText(args[1], runtime.LayoutFactory, runtime.RestoreWorkflowFactory);
        }

        if (args.Length == 1 && args[0] == "--restore-view")
        {
            return RunRestoreView(runtime.LayoutFactory, runtime.RestoreWorkflowFactory);
        }

        if (args.Length == 1 && args[0] == "--dictionary-ui")
        {
            return RunDictionaryUi(runtime.LayoutFactory);
        }

        if (args.Length == 4 && args[0] == "--sanitize" && args[2] == "--dictionary")
        {
            return RunSanitizeWithDictionary(args[1], args[3], runtime.SanitizerFactory);
        }

        if (args.Length == 1 && args[0] == "--doctor")
        {
            return RunDoctor(null);
        }

        if (args.Length == 5 && args[0] == "--doctor" && args[1] == "--package")
        {
            return RunDoctor(new MvpPackageManifest(args[2], args[3], args[4]));
        }

        if (args.Length is 3 or 4 && args[0] == "--dictionary-add")
        {
            return RunDictionaryAdd(args[1], args[2], args.Length == 4 ? args[3] : null, runtime.LayoutFactory);
        }

        if (args.Length >= 3 && args[0] == "--dictionary-add-batch")
        {
            return RunDictionaryAddBatch(args.Skip(1).ToArray(), runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--dictionary-list")
        {
            return RunDictionaryList(reveal: false, runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--dictionary-list" && args[1] == "--reveal")
        {
            return RunDictionaryList(reveal: true, runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--dictionary-import")
        {
            return RunDictionaryImport(args[1], runtime.LayoutFactory);
        }

        if (args.Length >= 2 && args[0] == "--dictionary-remove")
        {
            return RunDictionaryRemove(args.Skip(1).ToArray(), runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--hotkey-show")
        {
            return RunHotkeyShow(runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--hotkey-set")
        {
            return RunHotkeySet(args[1], runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--send-mode-show")
        {
            return RunSendModeShow(runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--send-mode-enable")
        {
            return RunSendModeEnable(runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--send-mode-disable")
        {
            return RunSendModeDisable(runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--autostart-show")
        {
            return RunAutostartShow();
        }

        if (args.Length == 1 && args[0] == "--autostart-enable")
        {
            return RunAutostartEnable();
        }

        if (args.Length == 1 && args[0] == "--autostart-disable")
        {
            return RunAutostartDisable();
        }

        if (args.Length is 1 or 2 && args[0] == "--local-data-cleanup")
        {
            var confirmed = args.Length == 2 && args[1] == LocalDataCleanup.ConfirmationFlag;
            return args.Length == 1 || confirmed
                ? RunLocalDataCleanup(confirmed, runtime.LayoutFactory)
                : Fail($"Expected --local-data-cleanup [{LocalDataCleanup.ConfirmationFlag}].");
        }

        if (args.Length == 2 && args[0] == "--policy-add-url-prefix")
        {
            return RunPolicyAddUrlPrefix(args[1], runtime.LayoutFactory);
        }

        if (args.Length == 3 && args[0] == "--policy-add-regex")
        {
            return RunPolicyAddRegex(args[1], args[2], runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--rules-export")
        {
            return RunRulesExport(args[1], runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--policy-diagnostics")
        {
            return RunPolicyDiagnostics(runtime.PolicyLoadFactory);
        }

        if (args.Length is 2 or 3 && args[0] == "--policy-test")
        {
            var includeSanitizedText = args.Length == 3 && args[2] == "--show-sanitized";
            return args.Length == 2 || includeSanitizedText
                ? RunPolicyTest(args[1], includeSanitizedText, runtime.PolicyLoadFactory, runtime.PolicyTestSanitizerFactory)
                : Fail("Expected --policy-test \"text\" [--show-sanitized].");
        }

        if (args.Length == 1 && args[0] == "--audit-summary")
        {
            return RunAuditSummary();
        }

        if (args.Length == 1 && args[0] == "--audit-view")
        {
            return RunAuditView(runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--audit-verify")
        {
            return RunAuditVerify(runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--project-workspace-protect")
        {
            return RunProjectWorkspaceProtect(args[1], runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--project-workspace-status")
        {
            return RunProjectWorkspaceStatus(args[1], runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--project-file-sanitize")
        {
            return RunProjectFileSanitize(
                args[1],
                workspacePath: null,
                requireProtectedWorkspace: false,
                runtime.LayoutFactory,
                runtime.SanitizerFactory);
        }

        if (args.Length == 4 && args[0] == "--project-file-sanitize" && args[2] == "--protected-workspace")
        {
            return RunProjectFileSanitize(
                args[1],
                args[3],
                requireProtectedWorkspace: true,
                runtime.LayoutFactory,
                runtime.SanitizerFactory);
        }

        if (args.Length == 1 && args[0] == "--project-file-smoke")
        {
            return RunProjectFileSmoke();
        }

        if (args.Length == 3 && args[0] == "--project-tool-output-sanitize")
        {
            return RunProjectToolOutputSanitize(
                args[1],
                args[2],
                runtime.LayoutFactory,
                runtime.SanitizerFactory);
        }

        if (args.Length == 2 && args[0] == "--project-tool-output-unmanaged")
        {
            return RunProjectToolOutputUnmanaged(args[1]);
        }

        if (args.Length == 8
            && args[0] == "--project-patch-dry-run"
            && args[2] == "--protected-workspace"
            && args[4] == "--source-content-hash"
            && args[6] == "--sanitized-edit")
        {
            return RunProjectPatchDryRun(
                args[1],
                args[3],
                args[5],
                args[7],
                runtime.LayoutFactory,
                runtime.SanitizerFactory,
                runtime.RestoreWorkflowFactory);
        }

        if (args.Length == 9
            && args[0] == "--project-patch-apply"
            && args[2] == "--protected-workspace"
            && args[4] == "--source-content-hash"
            && args[6] == "--sanitized-edit"
            && args[8] is "--approve" or "--cancel")
        {
            return RunProjectPatchApply(
                args[1],
                args[3],
                args[5],
                args[7],
                approved: args[8] == "--approve",
                runtime.LayoutFactory,
                runtime.SanitizerFactory,
                runtime.RestoreWorkflowFactory);
        }

        if (args.Length == 2 && args[0] == "--project-attachment-bypass-status")
        {
            return RunProjectAttachmentBypassStatus(args[1], runtime.LayoutFactory);
        }

        if (args.Length == 2 && args[0] == "--project-connector-bypass-status")
        {
            return RunProjectConnectorBypassStatus(args[1], runtime.LayoutFactory);
        }

        if (args.Length == 1 && args[0] == "--project-file-product-smoke")
        {
            return RunProjectFileProductSmoke();
        }

        if (args.Length == 3 && args[0] == "--audit-cleanup" && args[1] == "--keep")
        {
            return int.TryParse(args[2], out var keepEvents) && keepEvents >= 0
                ? RunAuditCleanup(keepEvents, runtime.LayoutFactory)
                : Fail("Expected --audit-cleanup --keep non-negative-event-count.");
        }

        if (args.Length == 1 && args[0] == "--crash-reports")
        {
            return RunCrashReports();
        }

        if ((args.Length == 1 && args[0] == "--tray-app")
            || (args.Length == 2 && args[0] == "--tray-app" && args[1] == "--global"))
        {
            var layout = runtime.LayoutFactory();
            return WindowsTrayApp.Run(
                runtime.SanitizerFactory(Array.Empty<DictionaryTerm>()),
                layout,
                useGlobalMutex: args.Length == 2);
        }

        if (args.Length == 1 && args[0] == "--os-profiles-list")
        {
            return RunOsProfilesList();
        }

        if (args.Length == 1 && args[0] == "--os-compatibility-matrix")
        {
            return RunOsCompatibilityMatrix();
        }

        if (args.Length == 1 && args[0] == "--os-surface-diagnostic")
        {
            return RunOsSurfaceDiagnostic();
        }

        if (args.Length == 1 && args[0] == "--os-composer-diagnostic")
        {
            return RunOsComposerDiagnostic(TimeSpan.Zero);
        }

        if (args.Length == 2 && args[0] == "--os-composer-diagnostic-delay")
        {
            return int.TryParse(args[1], out var delaySeconds) && delaySeconds >= 0
                ? RunOsComposerDiagnostic(TimeSpan.FromSeconds(Math.Min(delaySeconds, 30)))
                : Fail("Expected non-negative delay in seconds.");
        }

        if (args.Length == 2 && args[0] == "--os-demo-dry-run")
        {
            return RunOsDemoDryRun(args[1], runtime.SanitizerFactory);
        }

        if (args.Length == 1 && args[0] == "--os-demo-smoke")
        {
            return RunOsDemoSmoke();
        }

        if (args.Length == 1 && args[0] == "--product-smoke")
        {
            return RunProductSmoke();
        }

        if (args.Length == 1 && args[0] == "--native-profiles-status")
        {
            return RunNativeProfilesStatus(runtime.LayoutFactory);
        }

        if (args.Length == 4 && args[0] == "--native-profile-verify")
        {
            return Fail("Use --native-profile-verify-delay so verification runs after you focus the target Codex/ChatGPT composer.");
        }

        if (args.Length == 5 && args[0] == "--native-profile-verify-delay")
        {
            return int.TryParse(args[4], out var delaySeconds) && delaySeconds >= 0
                ? RunNativeProfileVerify(
                    args[1],
                    args[2],
                    args[3],
                    TimeSpan.FromSeconds(Math.Min(delaySeconds, 30)),
                    runtime.LayoutFactory)
                : Fail("Expected --native-profile-verify-delay profile-id submit-binding newline-binding non-negative-delay-seconds.");
        }

        if (args.Length == 1 && args[0] == "--native-submit-smoke")
        {
            return RunNativeSubmitSmoke();
        }

        if (args.Length == 1 && args[0] == "--os-demo-send-gate")
        {
            return RunOsDemoSendGate();
        }

        if (args.Length == 1 && args[0] == "--os-demo-local-target")
        {
            return LocalOsDemoTarget.Run();
        }

        if (args.Length == 1 && args[0] == "--os-demo-hotkey")
        {
            return WindowsHotkeyDemoLoop.Run(
                runtime.SanitizerFactory(Array.Empty<DictionaryTerm>()),
                WindowsHotkeyDemoLoop.WindowsHotkeyDemoMode.DryRun);
        }

        if (args.Length == 1 && args[0] == "--os-demo-hotkey-apply")
        {
            return WindowsHotkeyDemoLoop.Run(
                runtime.SanitizerFactory(Array.Empty<DictionaryTerm>()),
                WindowsHotkeyDemoLoop.WindowsHotkeyDemoMode.ApplyOnly);
        }

        if (args.Length == 1 && args[0] == "--os-demo-hotkey-send")
        {
            return WindowsHotkeyDemoLoop.Run(
                runtime.SanitizerFactory(Array.Empty<DictionaryTerm>()),
                WindowsHotkeyDemoLoop.WindowsHotkeyDemoMode.ConfirmAndSend);
        }

        PrintHelp();
        return 0;
    }

    private static CliRuntime CreateDefaultRuntime()
    {
        return new CliRuntime(
            Sanitizer.CreateProduction,
            () => Sanitizer.LoadProductionPolicy(DefaultStorageLayout.CreateDefault()),
            Sanitizer.CreateProduction,
            DefaultStorageLayout.CreateDefault,
            LocalRestoreWorkflow.CreateProduction);
    }

    private static CliRuntime CreateRuntime(
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory,
        Func<DefaultStorageLayout> layoutFactory)
    {
        return new CliRuntime(
            sanitizerFactory,
            () => Sanitizer.LoadProductionPolicy(layoutFactory()),
            _ => sanitizerFactory(Array.Empty<DictionaryTerm>()),
            layoutFactory,
            LocalRestoreWorkflow.CreateProduction);
    }

    private static int RunSanitize(
        string text,
        IReadOnlyList<DictionaryTerm> dictionaryTerms,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory)
    {
        var sanitizer = sanitizerFactory(dictionaryTerms);
        var result = sanitizer.Sanitize(CreatePromptRequest(text));

        Console.WriteLine($"decision: {CliOutputFormatting.FormatDecision(result.Decision)}");
        Console.WriteLine($"sanitized_text: {result.SanitizedText}");
        return 0;
    }

    private static int RunRestoreText(
        string text,
        Func<DefaultStorageLayout> layoutFactory,
        Func<DefaultStorageLayout, LocalRestoreWorkflow> restoreWorkflowFactory)
    {
        var result = restoreWorkflowFactory(layoutFactory()).RestoreText(text);

        Console.WriteLine($"status: {(result.Restoration.Metadata.LocalSensitive ? "local_sensitive_restored" : "no_local_values_restored")}");
        foreach (var item in result.Restoration.Metadata.RestoredPseudonymCountsByType.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"restored.{item.Key}: {item.Value}");
        }

        foreach (var warning in result.Restoration.Warnings.DistinctBy(warning => warning.Code).OrderBy(warning => warning.Code, StringComparer.Ordinal))
        {
            Console.WriteLine($"warning.{warning.Code}: {warning.Severity}");
        }

        Console.WriteLine("output:");
        Console.WriteLine(result.DisplayText);
        return 0;
    }

    private static int RunRestoreView(
        Func<DefaultStorageLayout> layoutFactory,
        Func<DefaultStorageLayout, LocalRestoreWorkflow> restoreWorkflowFactory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Local restore view requires Windows.");
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new LocalRestoreForm(restoreWorkflowFactory(layoutFactory()));
        Application.Run(form);
        return 0;
    }

    private static int RunDictionaryUi(Func<DefaultStorageLayout> layoutFactory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("Sensitive terms UI requires Windows.");
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new DictionaryManagementForm(layoutFactory());
        Application.Run(form);
        return 0;
    }

    private static int RunSanitizeWithDictionary(
        string text,
        string dictionaryFilePath,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory)
    {
        var dictionaryResult = new CsvDictionaryLoader().LoadOrDefault(dictionaryFilePath);

        if (!dictionaryResult.Activated || !dictionaryResult.LoadedFromFile)
        {
            Console.Error.WriteLine("CSV dictionary could not be activated.");
            return 1;
        }

        return RunSanitize(text, dictionaryResult.ActiveTerms, sanitizerFactory);
    }

    private static int RunDoctor(MvpPackageManifest? manifest)
    {
        var report = ReadinessDoctor.Check(DefaultStorageLayout.CreateDefault(), manifest);

        Console.WriteLine($"ready: {report.Ready.ToString().ToLowerInvariant()}");
        foreach (var item in report.Items)
        {
            Console.WriteLine($"{item.Component}: {item.Status} {item.Code}");
        }

        return report.Ready ? 0 : 1;
    }

    private static int RunDictionaryAdd(
        string type,
        string value,
        string? notes,
        Func<DefaultStorageLayout> layoutFactory)
    {
        var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layoutFactory()));
        var result = store.Add(type, value, notes);
        Console.WriteLine($"status: {result.Code}");
        if (result.EntryId is not null)
        {
            Console.WriteLine($"id: {result.EntryId}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunDictionaryAddBatch(string[] args, Func<DefaultStorageLayout> layoutFactory)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            return Fail("Expected --dictionary-add-batch type value [type value]...");
        }

        var terms = args
            .Chunk(2)
            .Select(pair => new DictionaryTerm(pair[0], pair[1], PolicyActions.PseudonymizeRestorable, null))
            .ToArray();
        var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layoutFactory()));
        var result = store.AddBatch(terms);

        Console.WriteLine($"status: {result.Code}");
        foreach (var item in result.Items)
        {
            Console.WriteLine($"item type={item.Type} status={item.Code} value_length={item.ValueLength}");
            if (item.EntryId is not null)
            {
                Console.WriteLine($"id: {item.EntryId}");
            }
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunOsComposerDiagnostic(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Console.WriteLine($"status: waiting_for_focus");
            Console.WriteLine($"delay_seconds: {delay.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}");
            Console.WriteLine("Focus the target composer before the delay ends.");
            System.Threading.Thread.Sleep(delay);
        }

        var result = WindowsFocusedComposerDiscovery.CreateDefault().DiscoverActiveSurface();

        Console.WriteLine($"status: {result.Status}");
        if (result.Surface is not null)
        {
            Console.WriteLine($"profile_id: {result.Surface.ProfileId}");
            Console.WriteLine($"can_capture: {result.Surface.CanCaptureText.ToString().ToLowerInvariant()}");
            Console.WriteLine($"can_replace: {result.Surface.CanReplaceText.ToString().ToLowerInvariant()}");
            Console.WriteLine($"can_submit: {result.Surface.CanSubmit.ToString().ToLowerInvariant()}");
            foreach (var item in result.Surface.Metadata.ToDictionary().OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{item.Key}: {item.Value}");
            }
        }

        foreach (var item in result.Diagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunDictionaryList(bool reveal, Func<DefaultStorageLayout> layoutFactory)
    {
        var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layoutFactory()));
        if (reveal)
        {
            Console.WriteLine("warning: local_sensitive_values_revealed");
            foreach (var entry in store.ListEntriesForLocalReveal())
            {
                Console.WriteLine($"{entry.Id} type={entry.Type} action={entry.Action} value={entry.Value}");
            }

            return 0;
        }

        foreach (var entry in store.ListSummaries())
        {
            Console.WriteLine($"{entry.Id} type={entry.Type} action={entry.Action} value_length={entry.ValueLength}");
        }

        return 0;
    }

    private static int RunDictionaryImport(string dictionaryFilePath, Func<DefaultStorageLayout> layoutFactory)
    {
        var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layoutFactory()));
        var result = store.ImportCsv(dictionaryFilePath);

        var imported = result.Activated && result.LoadedFromFile;
        Console.WriteLine($"status: {(imported ? "dictionary_imported" : "dictionary_import_rejected")}");
        Console.WriteLine($"term_count: {result.ActiveTerms.Count}");
        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"warning: {warning.Code}");
        }

        return imported ? 0 : 1;
    }

    private static int RunDictionaryRemove(string[] ids, Func<DefaultStorageLayout> layoutFactory)
    {
        var store = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layoutFactory()));
        var result = store.RemoveBatch(ids);
        Console.WriteLine($"status: {result.Code}");
        foreach (var item in result.Items)
        {
            Console.WriteLine($"item id={item.EntryId} status={item.Code}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunPolicyAddUrlPrefix(string prefix, Func<DefaultStorageLayout> layoutFactory)
    {
        var rules = new ManagedPolicyRules(layoutFactory().PolicyDirectory);
        var result = rules.AddUrlPrefix(prefix);
        Console.WriteLine($"status: {result.Code}");
        return result.Succeeded ? 0 : 1;
    }

    private static int RunHotkeyShow(Func<DefaultStorageLayout> layoutFactory)
    {
        var layout = layoutFactory();
        var result = HotkeySettingsStore.Load(layout);
        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"hotkey: {result.Settings.ProtectionHotkey.Binding.DisplayText}");
        Console.WriteLine($"manual_scan_hotkey: {result.Settings.ProtectionHotkey.Binding.DisplayText}");
        Console.WriteLine($"source: {HotkeySettingsStore.DefaultPath(layout)}");
        return result.Usable ? 0 : 1;
    }

    private static int RunHotkeySet(string hotkeyText, Func<DefaultStorageLayout> layoutFactory)
    {
        var result = HotkeySettingsStore.SaveProtectionHotkey(layoutFactory(), hotkeyText);
        Console.WriteLine($"status: {result.Code}");
        if (result.Hotkey is not null)
        {
            Console.WriteLine($"hotkey: {result.Hotkey.Binding.DisplayText}");
            Console.WriteLine($"manual_scan_hotkey: {result.Hotkey.Binding.DisplayText}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunSendModeShow(Func<DefaultStorageLayout> layoutFactory)
    {
        var result = LiveOsDemoEvidence.Check(layoutFactory());
        PrintSendModeResult(result);
        return 0;
    }

    private static int RunSendModeEnable(Func<DefaultStorageLayout> layoutFactory)
    {
        var result = LiveOsDemoEvidence.EnableSendMode(layoutFactory());
        PrintSendModeResult(result);
        return result.Enabled ? 0 : 1;
    }

    private static int RunSendModeDisable(Func<DefaultStorageLayout> layoutFactory)
    {
        var result = LiveOsDemoEvidence.DisableSendMode(layoutFactory());
        PrintSendModeResult(result);
        return 0;
    }

    private static void PrintSendModeResult(LiveOsDemoSendGateResult result)
    {
        Console.WriteLine($"status: {result.Status}");
        Console.WriteLine($"enabled: {result.Enabled.ToString().ToLowerInvariant()}");
        foreach (var diagnostic in result.Diagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{diagnostic.Key}: {diagnostic.Value}");
        }
    }

    private static int RunAutostartShow()
    {
        var state = AutostartManager.Show(new WindowsRunStartupRegistration(), BuildTrayCommandLine());
        PrintAutostartState(state);
        return 0;
    }

    private static int RunAutostartEnable()
    {
        var state = AutostartManager.Enable(new WindowsRunStartupRegistration(), BuildTrayCommandLine());
        PrintAutostartState(state);
        return state.Enabled ? 0 : 1;
    }

    private static int RunAutostartDisable()
    {
        var state = AutostartManager.Disable(new WindowsRunStartupRegistration(), BuildTrayCommandLine());
        PrintAutostartState(state);
        return 0;
    }

    private static void PrintAutostartState(AutostartState state)
    {
        Console.WriteLine($"status: {state.Code}");
        Console.WriteLine($"enabled: {state.Enabled.ToString().ToLowerInvariant()}");
        Console.WriteLine($"registry_value: {state.RegistryValueName}");
        Console.WriteLine($"expected_command_length: {state.ExpectedCommandLine.Length}");
        Console.WriteLine($"configured_command_length: {(state.ConfiguredCommandLine?.Length ?? 0)}");
    }

    private static string BuildTrayCommandLine()
    {
        var appBaseDirectory = AppContext.BaseDirectory;
        var trayExecutablePath = Path.Combine(appBaseDirectory, "CodexRedactionGate.Tray.exe");
        if (File.Exists(trayExecutablePath))
        {
            return $"\"{trayExecutablePath}\"";
        }

        var processPath = Environment.ProcessPath;
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            && assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{processPath}\" \"{assemblyPath}\" --tray-app";
        }

        var executablePath = !string.IsNullOrWhiteSpace(processPath) ? processPath : assemblyPath;
        return $"\"{executablePath}\" --tray-app";
    }

    private static int RunLocalDataCleanup(bool confirmed, Func<DefaultStorageLayout> layoutFactory)
    {
        var report = confirmed
            ? LocalDataCleanup.Delete(layoutFactory(), confirmed: true)
            : LocalDataCleanup.Plan(layoutFactory());
        PrintLocalDataCleanupReport(report);
        return report.Succeeded ? 0 : 1;
    }

    private static void PrintLocalDataCleanupReport(LocalDataCleanupReport report)
    {
        Console.WriteLine($"status: {report.Code}");
        Console.WriteLine($"deleted: {report.Deleted.ToString().ToLowerInvariant()}");
        Console.WriteLine($"root_path_length: {report.RootDirectory.Length}");
        Console.WriteLine($"planned_directory_count: {report.PlannedDirectories.Count}");
        Console.WriteLine($"deleted_directory_count: {report.DeletedDirectories.Count}");
    }

    private static int RunPolicyAddRegex(string type, string pattern, Func<DefaultStorageLayout> layoutFactory)
    {
        var rules = new ManagedPolicyRules(layoutFactory().PolicyDirectory);
        var result = rules.AddRegexRule(type, pattern);
        Console.WriteLine($"status: {result.Code}");
        return result.Succeeded ? 0 : 1;
    }

    private static int RunRulesExport(string exportDirectory, Func<DefaultStorageLayout> layoutFactory)
    {
        var result = RuleSetExporter.Export(layoutFactory(), exportDirectory);
        Console.WriteLine($"status: {result.Code}");
        foreach (var file in result.ExportedFiles.OrderBy(file => file, StringComparer.Ordinal))
        {
            Console.WriteLine($"file: {file}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunPolicyDiagnostics(Func<ManagedPolicyLoadResult> policyLoadFactory)
    {
        var result = policyLoadFactory();
        Console.WriteLine($"source: {result.Source}");
        Console.WriteLine($"loaded_from_file: {result.LoadedFromFile.ToString().ToLowerInvariant()}");
        Console.WriteLine($"activated: {result.Activated.ToString().ToLowerInvariant()}");
        foreach (var profile in result.Diagnostics.ActiveProfileIds)
        {
            Console.WriteLine($"profile: {profile}");
        }

        foreach (var item in result.Diagnostics.RuleCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"rule_count.{item.Key}: {item.Value}");
        }

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"warning: {warning.Code}");
        }

        return result.Activated ? 0 : 1;
    }

    private static int RunPolicyTest(
        string text,
        bool includeSanitizedText,
        Func<ManagedPolicyLoadResult> policyLoadFactory,
        Func<ManagedPolicyLoadResult, Sanitizer> policyTestSanitizerFactory)
    {
        var policy = policyLoadFactory();
        var sanitizer = policyTestSanitizerFactory(policy);
        var result = sanitizer.Sanitize(CreatePromptRequest(text));
        foreach (var line in PolicyTestReporter.Render(result, policy, includeSanitizedText))
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static int RunAuditSummary()
    {
        var summary = AuditSummaryReporter.Summarize(DefaultStorageLayout.CreateDefault().AuditDirectory);
        Console.WriteLine($"chain: {summary.Chain.Code}");
        Console.WriteLine($"events: {summary.Chain.EventCount}");
        if (summary.FirstEvent is not null)
        {
            Console.WriteLine($"first_event_utc: {summary.FirstEvent:O}");
        }

        if (summary.LastEvent is not null)
        {
            Console.WriteLine($"last_event_utc: {summary.LastEvent:O}");
        }

        foreach (var item in summary.DecisionCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"decision.{item.Key}: {item.Value}");
        }

        foreach (var item in summary.WarningCodeCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"warning.{item.Key}: {item.Value}");
        }

        return summary.Chain.Valid ? 0 : 1;
    }

    private static int RunAuditView(Func<DefaultStorageLayout> layoutFactory)
    {
        var report = AuditViewer.Load(layoutFactory().AuditDirectory);
        foreach (var line in AuditViewer.Render(report))
        {
            Console.WriteLine(line);
        }

        return report.Chain.Valid ? 0 : 1;
    }

    private static int RunAuditVerify(Func<DefaultStorageLayout> layoutFactory)
    {
        var verification = AuditChainVerifier.Verify(layoutFactory().AuditDirectory);
        Console.WriteLine($"chain: {verification.Code}");
        Console.WriteLine($"events: {verification.EventCount}");
        return verification.Valid ? 0 : 1;
    }

    private static int RunAuditCleanup(int keepEvents, Func<DefaultStorageLayout> layoutFactory)
    {
        var cleanup = AuditViewer.Cleanup(layoutFactory().AuditDirectory, keepEvents);
        Console.WriteLine($"status: {(cleanup.Chain.Valid ? "audit_cleanup_complete" : "audit_cleanup_chain_invalid")}");
        Console.WriteLine($"events_before: {cleanup.EventsBefore}");
        Console.WriteLine($"events_deleted: {cleanup.EventsDeleted}");
        Console.WriteLine($"events_kept: {cleanup.EventsKept}");
        Console.WriteLine($"chain: {cleanup.Chain.Code}");
        return cleanup.Chain.Valid ? 0 : 1;
    }

    private static int RunCrashReports()
    {
        var crashDiag = new LocalCrashDiagnostics(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexRedactionGate",
            "crashes"));
        var reports = crashDiag.LoadReports();

        if (reports.Count == 0)
        {
            Console.WriteLine("status: no_crash_reports");
            return 0;
        }

        Console.WriteLine($"total_reports: {reports.Count}");
        foreach (var report in reports)
        {
            Console.WriteLine($"timestamp: {report.Timestamp:O}");
            Console.WriteLine($"component: {report.Component}");
            Console.WriteLine($"exception_type: {report.ExceptionType}");
            Console.WriteLine($"status_code: {report.StatusCode}");
            Console.WriteLine($"build_version: {report.BuildVersion}");
            Console.WriteLine("---");
        }

        return 0;
    }

    private static int RunOsProfilesList()
    {
        foreach (var profile in SurfaceProfileCatalog.Default.Profiles.OrderBy(profile => profile.ProfileId, StringComparer.Ordinal))
        {
            Console.WriteLine($"{profile.ProfileId} name=\"{profile.DisplayName}\" read={profile.ReadStrategy} write={profile.WriteStrategy} submit={profile.SubmitStrategy}");
        }

        return 0;
    }

    private static int RunOsCompatibilityMatrix()
    {
        foreach (var line in SurfaceCompatibilityMatrix.Render())
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static int RunOsSurfaceDiagnostic()
    {
        var result = WindowsActiveSurfaceDiscovery.CreateDefault().DiscoverActiveSurface();

        Console.WriteLine($"status: {result.Status}");
        if (result.Surface is not null)
        {
            Console.WriteLine($"profile_id: {result.Surface.ProfileId}");
            Console.WriteLine($"can_capture: {result.Surface.CanCaptureText.ToString().ToLowerInvariant()}");
            Console.WriteLine($"can_replace: {result.Surface.CanReplaceText.ToString().ToLowerInvariant()}");
            Console.WriteLine($"can_submit: {result.Surface.CanSubmit.ToString().ToLowerInvariant()}");
            foreach (var item in result.Surface.Metadata.ToDictionary().OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"{item.Key}: {item.Value}");
            }
        }

        foreach (var item in result.Diagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunOsDemoDryRun(
        string text,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory)
    {
        var result = OsAdapterDemoRunner.RunDryRun(sanitizerFactory(Array.Empty<DictionaryTerm>()), text);

        Console.WriteLine($"status: {result.Status}");
        if (result.SanitizationResult is not null)
        {
            Console.WriteLine($"decision: {CliOutputFormatting.FormatDecision(result.SanitizationResult.Decision)}");
            Console.WriteLine($"replacement_count: {result.SanitizationResult.Replacements.Count}");
        }

        if (result.ConfirmationModel is not null)
        {
            Console.WriteLine(OsConfirmationOverlayRenderer.RenderText(result.ConfirmationModel));
        }

        return result.Status == OsInteractionStatusIds.Blocked
            || result.Status == OsInteractionStatusIds.CaptureFailed
            || result.Status == OsInteractionStatusIds.UnsupportedSurface
            || result.Status == OsInteractionStatusIds.UnsupportedPlatform
            ? 1
            : 0;
    }

    private static int RunOsDemoSmoke()
    {
        var report = OsAdapterDemoRunner.RunSmoke(System.Text.Encoding.UTF8.GetBytes("os-demo-smoke-secret"));

        Console.WriteLine($"passed: {report.Passed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"dry_run: {report.DryRunPassed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"apply_only: {report.ApplyOnlyPassed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"confirm_and_send_disabled_by_default: {report.ConfirmAndSendDisabledByDefaultPassed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"confirm_and_send: {report.ConfirmAndSendPassed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"cancel: {report.CancelPassed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"block: {report.BlockPassed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"write_failure: {report.WriteFailurePassed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"audit_raw_free: {report.AuditRawFreePassed.ToString().ToLowerInvariant()}");

        return report.Passed ? 0 : 1;
    }

    private static int RunProductSmoke()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-product-smoke", Guid.NewGuid().ToString("N"));
        try
        {
            var report = ProductSmokeRunner.RunInstalledArtifactSmoke(
                AppContext.BaseDirectory,
                Path.Combine(tempDirectory, "installed"),
                DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data")),
                System.Text.Encoding.UTF8.GetBytes("product-smoke-secret"));
            foreach (var line in ProductSmokeRunner.RenderRawFree(report))
            {
                Console.WriteLine(line);
            }

            return report.Passed ? 0 : 1;
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static int RunNativeProfilesStatus(Func<DefaultStorageLayout> layoutFactory)
    {
        var layout = layoutFactory();
        var result = SubmitBindingProfileStore.Load(layout);
        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"profile_count: {result.Profiles.Count}");
        Console.WriteLine($"profile_store_path_length: {SubmitBindingProfileStore.DefaultPath(layout).Length}");
        Console.WriteLine("project_files_protected: false");
        Console.WriteLine($"project_file_status: {ProjectFileProtectionStatusValues.NotConfigured}");
        foreach (var profile in result.Profiles.OrderBy(profile => profile.ProfileId, StringComparer.Ordinal))
        {
            var composerProtected = profile.CapabilityStatus == OsInteractionStatusIds.Protected;
            Console.WriteLine($"profile={profile.ProfileId} readiness={profile.CapabilityStatus} capability_status={profile.CapabilityStatus} composer_protected={composerProtected.ToString().ToLowerInvariant()} project_files_protected=false binding_source={profile.BindingSource} protected_send_binding={profile.SubmitBinding?.DisplayText ?? "unknown"} newline_binding={profile.NewlineBinding?.DisplayText ?? "unknown"}");
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunProjectWorkspaceProtect(string workspacePath, Func<DefaultStorageLayout> layoutFactory)
    {
        var result = ProtectedWorkspaceStore.Protect(layoutFactory(), workspacePath);
        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"workspace_id: {result.WorkspaceId}");
        Console.WriteLine($"store_path_length: {result.StorePath.Length}");
        Console.WriteLine("raw_workspace_path_recorded_in_output: false");
        return result.Succeeded ? 0 : 1;
    }

    private static int RunProjectWorkspaceStatus(string workspacePath, Func<DefaultStorageLayout> layoutFactory)
    {
        var result = ProtectedWorkspaceStore.GetStatus(layoutFactory(), workspacePath);
        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"protected_workspace: {result.Protected.ToString().ToLowerInvariant()}");
        Console.WriteLine($"workspace_id: {result.WorkspaceId}");
        Console.WriteLine("project_files_protected: false");
        Console.WriteLine($"project_file_status: {ProjectFileProtectionStatusValues.BrokerDemoOnly}");
        Console.WriteLine("raw_workspace_path_recorded_in_output: false");
        return result.Protected ? 0 : 1;
    }

    private static int RunProjectFileSanitize(
        string filePath,
        string? workspacePath,
        bool requireProtectedWorkspace,
        Func<DefaultStorageLayout> layoutFactory,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory)
    {
        var broker = new ProjectFileContextBroker(
            sanitizerFactory(Array.Empty<DictionaryTerm>()),
            layoutFactory(),
            requireProtectedWorkspace
                ? ProjectFileBrokerOptions.ProtectedWorkspaceDefault
                : ProjectFileBrokerOptions.DemoDefault);
        var result = broker.CreateSanitizedVirtualFile(filePath, workspacePath);

        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"project_files_protected: false");
        Console.WriteLine($"project_file_status: {ProjectFileProtectionStatusValues.BrokerDemoOnly}");
        foreach (var item in result.Diagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }

        foreach (var warning in result.Warnings.DistinctBy(warning => warning.Code).OrderBy(warning => warning.Code, StringComparer.Ordinal))
        {
            Console.WriteLine($"warning.{warning.Code}: {warning.Severity}");
        }

        if (result.VirtualFile is not null)
        {
            Console.WriteLine($"virtual_path: {result.VirtualFile.VirtualPath}");
            Console.WriteLine($"content_hash: {result.VirtualFile.ContentHash}");
            Console.WriteLine($"decision: {CliOutputFormatting.FormatDecision(result.VirtualFile.Decision)}");
            Console.WriteLine($"replacement_count: {result.VirtualFile.ReplacementCount}");
            Console.WriteLine("sanitized_virtual_file:");
            Console.WriteLine(result.VirtualFile.SanitizedText);
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunProjectFileSmoke()
    {
        var report = ProjectFileReadOnlySmokeRunner.Run(System.Text.Encoding.UTF8.GetBytes("project-file-smoke-secret"));
        foreach (var line in ProjectFileReadOnlySmokeRunner.RenderRawFree(report))
        {
            Console.WriteLine(line);
        }

        return report.Passed ? 0 : 1;
    }

    private static int RunProjectToolOutputSanitize(
        string workspacePath,
        string toolOutput,
        Func<DefaultStorageLayout> layoutFactory,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory)
    {
        var broker = new ProjectFileContextBroker(
            sanitizerFactory(Array.Empty<DictionaryTerm>()),
            layoutFactory(),
            ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
        var result = broker.SanitizeManagedToolOutput(toolOutput, workspacePath);

        Console.WriteLine($"status: {result.Code}");
        WriteDiagnosticsAndWarnings(result.Diagnostics, result.Warnings);

        if (result.ToolOutput is not null)
        {
            Console.WriteLine($"decision: {CliOutputFormatting.FormatDecision(result.ToolOutput.Decision)}");
            Console.WriteLine($"replacement_count: {result.ToolOutput.ReplacementCount}");
            Console.WriteLine("sanitized_tool_output:");
            Console.WriteLine(result.ToolOutput.SanitizedText);
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunProjectToolOutputUnmanaged(string workspacePath)
    {
        var result = ProjectFileContextBroker.ReportUnmanagedToolOutput(workspacePath);

        Console.WriteLine($"status: {result.Code}");
        WriteDiagnosticsAndWarnings(result.Diagnostics, result.Warnings);

        return result.Succeeded ? 0 : 1;
    }

    private static int RunProjectPatchDryRun(
        string filePath,
        string workspacePath,
        string sourceContentHash,
        string sanitizedEdit,
        Func<DefaultStorageLayout> layoutFactory,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory,
        Func<DefaultStorageLayout, LocalRestoreWorkflow> restoreWorkflowFactory)
    {
        var layout = layoutFactory();
        var broker = new ProjectFileContextBroker(
            sanitizerFactory(Array.Empty<DictionaryTerm>()),
            layout,
            ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
        var source = broker.CreateSanitizedVirtualFile(filePath, workspacePath);
        if (source.VirtualFile is null)
        {
            Console.WriteLine($"status: {source.Code}");
            WriteDiagnosticsAndWarnings(source.Diagnostics, source.Warnings);

            return 1;
        }

        var dryRun = new ProjectFilePatchDryRun(
            request => restoreWorkflowFactory(layout).Restore(request).Restoration,
            layout);
        var sourceIdentity = source.VirtualFile with { ContentHash = sourceContentHash };
        var result = dryRun.Preview(new ProjectFilePatchDryRunRequest(sourceIdentity, workspacePath, filePath, sanitizedEdit));

        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"local_sensitive: {result.LocalSensitive.ToString().ToLowerInvariant()}");
        WriteDiagnosticsAndWarnings(result.Diagnostics, result.Warnings);

        if (result.PreviewText is not null)
        {
            Console.WriteLine("preview:");
            Console.WriteLine(result.PreviewText);
        }

        return result.Succeeded ? 0 : 1;
    }

    private static int RunProjectPatchApply(
        string filePath,
        string workspacePath,
        string sourceContentHash,
        string sanitizedEdit,
        bool approved,
        Func<DefaultStorageLayout> layoutFactory,
        Func<IReadOnlyList<DictionaryTerm>, Sanitizer> sanitizerFactory,
        Func<DefaultStorageLayout, LocalRestoreWorkflow> restoreWorkflowFactory)
    {
        var layout = layoutFactory();
        var broker = new ProjectFileContextBroker(
            sanitizerFactory(Array.Empty<DictionaryTerm>()),
            layout,
            ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
        var source = broker.CreateSanitizedVirtualFile(filePath, workspacePath);
        if (source.VirtualFile is null)
        {
            Console.WriteLine($"status: {source.Code}");
            WriteDiagnosticsAndWarnings(source.Diagnostics, source.Warnings);

            return 1;
        }

        var sourceIdentity = source.VirtualFile with { ContentHash = sourceContentHash };
        var dryRun = new ProjectFilePatchDryRun(
            request => restoreWorkflowFactory(layout).Restore(request).Restoration,
            layout);
        var applier = new ProjectFilePatchApplier(dryRun, new FileAuditSink(layout.AuditDirectory));
        var result = applier.Apply(new ProjectFilePatchApplyRequest(
            new ProjectFilePatchDryRunRequest(sourceIdentity, workspacePath, filePath, sanitizedEdit),
            approved));

        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"written: {result.Written.ToString().ToLowerInvariant()}");
        Console.WriteLine($"local_sensitive: {result.LocalSensitive.ToString().ToLowerInvariant()}");
        Console.WriteLine($"audit_written: {result.AuditWriteResult.Succeeded.ToString().ToLowerInvariant()}");
        WriteDiagnosticsAndWarnings(result.Diagnostics, result.Warnings);

        return result.Succeeded ? 0 : 1;
    }

    private static int RunProjectAttachmentBypassStatus(string workspacePath, Func<DefaultStorageLayout> layoutFactory)
    {
        var result = ProjectFileBypassGuard.ReportDirectAttachment(layoutFactory(), workspacePath);
        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"allowed: {result.Allowed.ToString().ToLowerInvariant()}");
        WriteDiagnosticsAndWarnings(result.Diagnostics, result.Warnings);
        return result.Allowed ? 0 : 1;
    }

    private static int RunProjectConnectorBypassStatus(string workspacePath, Func<DefaultStorageLayout> layoutFactory)
    {
        var result = ProjectFileBypassGuard.ReportUnmanagedConnector(layoutFactory(), workspacePath);
        Console.WriteLine($"status: {result.Code}");
        Console.WriteLine($"allowed: {result.Allowed.ToString().ToLowerInvariant()}");
        WriteDiagnosticsAndWarnings(result.Diagnostics, result.Warnings);
        return result.Allowed ? 0 : 1;
    }

    private static int RunProjectFileProductSmoke()
    {
        var report = ProjectFileProductSmokeRunner.Run(System.Text.Encoding.UTF8.GetBytes("project-file-product-smoke-secret"));
        foreach (var line in ProjectFileProductSmokeRunner.RenderRawFree(report))
        {
            Console.WriteLine(line);
        }

        return report.Passed ? 0 : 1;
    }

    private static void WriteDiagnosticsAndWarnings(
        IReadOnlyDictionary<string, string> diagnostics,
        IReadOnlyList<Warning> warnings)
    {
        foreach (var item in diagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }

        foreach (var warning in warnings.DistinctBy(warning => warning.Code).OrderBy(warning => warning.Code, StringComparer.Ordinal))
        {
            Console.WriteLine($"warning.{warning.Code}: {warning.Severity}");
        }
    }

    private static int RunNativeProfileVerify(
        string profileId,
        string submitBinding,
        string newlineBinding,
        TimeSpan delay,
        Func<DefaultStorageLayout> layoutFactory)
    {
        if (delay > TimeSpan.Zero)
        {
            Console.WriteLine("status: waiting_for_focus");
            Console.WriteLine($"delay_seconds: {(int)delay.TotalSeconds}");
            Console.WriteLine("Focus the target Codex/ChatGPT composer before the delay ends.");
            Thread.Sleep(delay);
        }

        var discovery = NativeProfileDiscoveryFactory();
        var profile = SubmitBindingOnboardingVerifier.VerifyUserBindings(
            profileId,
            submitBinding,
            newlineBinding,
            discovery);
        var save = SubmitBindingProfileStore.Upsert(layoutFactory(), profile);

        Console.WriteLine($"status: {profile.CapabilityStatus}");
        Console.WriteLine($"binding_source: {profile.BindingSource}");
        Console.WriteLine($"submit_binding: {profile.SubmitBinding?.DisplayText ?? "unknown"}");
        Console.WriteLine($"newline_binding: {profile.NewlineBinding?.DisplayText ?? "unknown"}");
        foreach (var item in profile.ToRawFreeDiagnostics().OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }

        return save.Succeeded && profile.CapabilityStatus == OsInteractionStatusIds.Protected ? 0 : 1;
    }

    private static int RunNativeSubmitSmoke()
    {
        var report = NativeSubmitProductSmokeRunner.Run(System.Text.Encoding.UTF8.GetBytes("native-submit-smoke-secret"));
        foreach (var line in NativeSubmitProductSmokeRunner.RenderRawFree(report))
        {
            Console.WriteLine(line);
        }

        return report.Passed ? 0 : 1;
    }

    private static int RunOsDemoSendGate()
    {
        var gate = LiveOsDemoEvidence.Check();
        Console.WriteLine($"status: {gate.Status}");
        Console.WriteLine($"enabled: {gate.Enabled.ToString().ToLowerInvariant()}");
        foreach (var item in gate.Diagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{item.Key}: {item.Value}");
        }

        return gate.Enabled ? 0 : 1;
    }

    private static int RunSelfTest(Func<Sanitizer> sanitizerFactory)
    {
        var sanitizer = sanitizerFactory();
        var allowInput = "Normal prompt text";
        var allowResult = sanitizer.Sanitize(CreatePromptRequest(allowInput));

        if (allowResult.Decision != SanitizeDecision.Allow)
        {
            return Fail("Expected allow decision.");
        }

        if (allowResult.SanitizedText != allowInput)
        {
            return Fail("Expected sanitized text to equal input.");
        }

        if (allowResult.Replacements.Count != 0)
        {
            return Fail("Expected zero replacements.");
        }

        if (allowResult.AuditEvent is null)
        {
            return Fail("Expected audit event.");
        }

        var confirmResult = sanitizer.Sanitize(CreatePromptRequest("Check SENSITIVE_MARKER"));

        if (confirmResult.Decision != SanitizeDecision.Confirm)
        {
            return Fail("Expected confirm decision.");
        }

        if (confirmResult.SanitizedText.Contains("SENSITIVE_MARKER", StringComparison.Ordinal))
        {
            return Fail("Expected synthetic marker to be replaced.");
        }

        if (!confirmResult.SanitizedText.Contains("SYNTHETIC_", StringComparison.Ordinal))
        {
            return Fail("Expected synthetic placeholder.");
        }

        if (confirmResult.Replacements.Count != 1)
        {
            return Fail("Expected one synthetic replacement.");
        }

        if (AuditInspection.Contains(confirmResult.AuditEvent, "Check SENSITIVE_MARKER")
            || AuditInspection.Contains(confirmResult.AuditEvent, "SENSITIVE_MARKER"))
        {
            return Fail("Expected confirm audit to exclude raw values.");
        }

        var blockResult = sanitizer.Sanitize(CreatePromptRequest("Reject BLOCK_THIS"));

        if (blockResult.Decision != SanitizeDecision.Block)
        {
            return Fail("Expected block decision.");
        }

        if (blockResult.Warnings.Count != 1)
        {
            return Fail("Expected one block warning.");
        }

        if (blockResult.Warnings[0].Code != "synthetic_block_marker")
        {
            return Fail("Expected synthetic block warning code.");
        }

        if (AuditInspection.Contains(blockResult.AuditEvent, "Reject BLOCK_THIS")
            || AuditInspection.Contains(blockResult.AuditEvent, "BLOCK_THIS"))
        {
            return Fail("Expected block audit to exclude raw values.");
        }

        var restoreVault = new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("self-test-secret"));
        var restorablePseudonym = restoreVault.GetOrCreatePseudonym("synthetic_marker", "RESTORE_ME");
        var restorer = new LocalRestorer(restoreVault);
        var restoreResult = restorer.Restore(new RestoreRequest(
            SanitizedText: $"Local value {restorablePseudonym}",
            Replacements: new[]
            {
                new Replacement(
                    ContentPartId: "prompt",
                    Offset: 12,
                    Length: restorablePseudonym.Length,
                    Type: "synthetic_marker",
                    Placeholder: restorablePseudonym,
                    Action: "pseudonymize_restorable",
                    Restorable: true)
            }));

        if (!restoreResult.Metadata.LocalSensitive
            || !restoreResult.Text.Contains("RESTORE_ME", StringComparison.Ordinal))
        {
            return Fail("Expected local restoration to restore and mark local-sensitive output.");
        }

        var restoreWorkflowDirectory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-self-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restoreWorkflowDirectory);
        var restoreWorkflow = new LocalRestoreWorkflow(
            new LocalRestorer(restoreVault),
            new FileAuditSink(Path.Combine(restoreWorkflowDirectory, "audit")));
        var workflowRestoreResult = restoreWorkflow.RestoreText($"Local response {restorablePseudonym} and TOKEN_REDACTED");

        if (!workflowRestoreResult.Restoration.Metadata.LocalSensitive
            || !workflowRestoreResult.DisplayText.Contains("LOCAL-SENSITIVE RESTORED OUTPUT", StringComparison.Ordinal)
            || !workflowRestoreResult.Restoration.Warnings.Any(warning => warning.Code == "non_restorable_redaction_skipped"))
        {
            return Fail("Expected local restore workflow to mark restored output and warn on non-restorable redactions.");
        }

        Directory.Delete(restoreWorkflowDirectory, recursive: true);

        var policyTestDirectory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-self-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(policyTestDirectory);

        try
        {
            var policyLoader = new TomlPolicyLoader();
            var missingPolicy = policyLoader.LoadOrDefault(Path.Combine(policyTestDirectory, "missing.toml"));

            if (!missingPolicy.Activated
                || missingPolicy.LoadedFromFile
                || missingPolicy.ActivePolicy.Defaults.Secret != PolicyActions.RedactNonRestorable)
            {
                return Fail("Expected missing policy to use safe built-in defaults.");
            }

            var validPolicyPath = Path.Combine(policyTestDirectory, "valid-policy.toml");
            File.WriteAllText(validPolicyPath, """
                version = 1
                profile = "self-test-policy"

                [defaults]
                unknown_high_risk = "confirm"
                secret = "redact_non_restorable"
                internal_identifier = "pseudonymize_restorable"

                [scanners]
                gitleaks_enabled = true
                gitleaks_timeout_ms = 5000
                """);
            var validPolicy = policyLoader.LoadOrDefault(validPolicyPath);

            if (!validPolicy.Activated
                || !validPolicy.LoadedFromFile
                || validPolicy.ActivePolicy.Profile != "self-test-policy")
            {
                return Fail("Expected valid policy to activate.");
            }

            var invalidPolicyPath = Path.Combine(policyTestDirectory, "invalid-policy.toml");
            File.WriteAllText(invalidPolicyPath, """
                version = 1
                profile = "SENSITIVE_MARKER"

                [defaults]
                secret = "SENSITIVE_MARKER"
                """);
            var invalidPolicy = policyLoader.LoadOrDefault(invalidPolicyPath, validPolicy.ActivePolicy);
            var invalidPolicyWarnings = string.Join(" ", invalidPolicy.Warnings.Select(warning => $"{warning.Code} {warning.Message}"));

            if (invalidPolicy.Activated
                || invalidPolicy.ActivePolicy.Profile != "self-test-policy"
                || invalidPolicyWarnings.Contains("SENSITIVE_MARKER", StringComparison.Ordinal))
            {
                return Fail("Expected invalid policy to be rejected without raw-value leakage.");
            }

            var regexPolicyPath = Path.Combine(policyTestDirectory, "regex-policy.toml");
            File.WriteAllText(regexPolicyPath, """
                version = 1
                profile = "regex-self-test-policy"

                [[regex]]
                type = "project"
                pattern = "\\bPRJ-[0-9]{4,}\\b"
                action = "pseudonymize_restorable"
                label = "internal project code"
                """);
            var regexPolicy = policyLoader.LoadOrDefault(regexPolicyPath, validPolicy.ActivePolicy);

            if (!regexPolicy.Activated
                || regexPolicy.ActivePolicy.RegexRules.Count != 1
                || regexPolicy.ActivePolicy.Profile != "regex-self-test-policy")
            {
                return Fail("Expected valid regex policy to activate.");
            }

            var invalidRegexPolicyPath = Path.Combine(policyTestDirectory, "invalid-regex-policy.toml");
            File.WriteAllText(invalidRegexPolicyPath, """
                version = 1
                profile = "SENSITIVE_MARKER"

                [[regex]]
                type = "project"
                pattern = "SENSITIVE_MARKER["
                action = "pseudonymize_restorable"
                """);
            var invalidRegexPolicy = policyLoader.LoadOrDefault(invalidRegexPolicyPath, regexPolicy.ActivePolicy);
            var invalidRegexPolicyWarnings = string.Join(" ", invalidRegexPolicy.Warnings.Select(warning => $"{warning.Code} {warning.Message}"));

            if (invalidRegexPolicy.Activated
                || invalidRegexPolicy.ActivePolicy.Profile != "regex-self-test-policy"
                || invalidRegexPolicyWarnings.Contains("SENSITIVE_MARKER", StringComparison.Ordinal))
            {
                return Fail("Expected invalid regex policy to be rejected without raw-value leakage.");
            }

            var dictionaryPath = Path.Combine(policyTestDirectory, "terms.csv");
            File.WriteAllText(dictionaryPath, """
                type,value,action,notes
                customer,ACME Banking,pseudonymize_restorable,Known customer
                """);
            var dictionaryResult = new CsvDictionaryLoader().LoadOrDefault(dictionaryPath);
            var dictionarySanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("dictionary-self-test-secret")),
                dictionaryResult.ActiveTerms);
            var dictionarySanitizeResult = dictionarySanitizer.Sanitize(CreatePromptRequest("Talk to ACME Banking"));

            if (!dictionaryResult.Activated
                || dictionarySanitizeResult.Decision != SanitizeDecision.Confirm
                || dictionarySanitizeResult.SanitizedText.Contains("ACME Banking", StringComparison.Ordinal)
                || AuditInspection.Contains(dictionarySanitizeResult.AuditEvent, "ACME Banking"))
            {
                return Fail("Expected CSV dictionary term to be pseudonymized without audit raw-value leakage.");
            }

            var invalidDictionaryPath = Path.Combine(policyTestDirectory, "invalid-terms.csv");
            File.WriteAllText(invalidDictionaryPath, """
                type,value,action,notes
                customer,ACME Banking,send_raw_prompt,Known customer
                """);
            var invalidDictionary = new CsvDictionaryLoader().LoadOrDefault(invalidDictionaryPath, dictionaryResult.ActiveTerms);
            var invalidDictionaryWarnings = string.Join(" ", invalidDictionary.Warnings.Select(warning => $"{warning.Code} {warning.Message}"));

            if (invalidDictionary.Activated
                || invalidDictionaryWarnings.Contains("ACME Banking", StringComparison.Ordinal))
            {
                return Fail("Expected invalid CSV dictionary to be rejected without raw-value leakage.");
            }

            var docsAllowlistPolicy = RedactionPolicy.BuiltInDefaults with
            {
                AllowRules = new[]
                {
                    new PolicyRule(
                        Type: "url",
                        Match: "https://learn.microsoft.com/",
                        Pattern: null,
                        Mode: "prefix",
                        Action: PolicyActions.Allow,
                        Reason: "public documentation",
                        Label: null)
                }
            };
            var technicalSanitizer = new Sanitizer(
                new InMemoryHmacMappingVault(System.Text.Encoding.UTF8.GetBytes("technical-self-test-secret")),
                Array.Empty<DictionaryTerm>(),
                docsAllowlistPolicy);
            var internalUrlResult = technicalSanitizer.Sanitize(CreatePromptRequest("Use https://deploy.corp.example.local/api"));

            if (internalUrlResult.Decision != SanitizeDecision.Confirm
                || internalUrlResult.SanitizedText.Contains("deploy.corp.example.local", StringComparison.Ordinal)
                || AuditInspection.Contains(internalUrlResult.AuditEvent, "deploy.corp.example.local"))
            {
                return Fail("Expected internal URL to be pseudonymized without audit raw-value leakage.");
            }

            var publicDocsResult = technicalSanitizer.Sanitize(CreatePromptRequest("Read https://learn.microsoft.com/en-us/dotnet/"));

            if (publicDocsResult.Decision != SanitizeDecision.Allow)
            {
                return Fail("Expected public allowlisted documentation URL to be allowed.");
            }

            var lookalikeResult = technicalSanitizer.Sanitize(CreatePromptRequest("Open https://learn.microsoft.com.evil.corp.local/docs"));

            if (lookalikeResult.Decision != SanitizeDecision.Confirm
                || lookalikeResult.SanitizedText.Contains("learn.microsoft.com.evil.corp.local", StringComparison.Ordinal))
            {
                return Fail("Expected internal lookalike domain to bypass public allowlist and be pseudonymized.");
            }
        }
        finally
        {
            Directory.Delete(policyTestDirectory, recursive: true);
        }

        Console.WriteLine("Self-test passed.");
        return 0;
    }

    private static SanitizeRequest CreatePromptRequest(string text)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart(
                    Id: "prompt",
                    ContentSource: ContentSources.PromptText,
                    RawText: text,
                    SourceMetadata: new Dictionary<string, string>())
            },
            Context: new SanitizationContext(
                Application: "self-test",
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: false,
                AllowSecretStorage: false,
                ConfirmationMode: "none"));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Codex Redaction Gate");
        Console.WriteLine("Usage:");
        Console.WriteLine("  --sanitize \"text\"");
        Console.WriteLine("  --sanitize \"text\" --dictionary \"terms.csv\"");
        Console.WriteLine("  --restore-text \"sanitized model response\"");
        Console.WriteLine("  --restore-view");
        Console.WriteLine("  --dictionary-ui");
        Console.WriteLine("  --doctor [--package app gitleaks provenance]");
        Console.WriteLine("  --dictionary-add type value [notes]");
        Console.WriteLine("  --dictionary-add-batch type value [type value]...");
        Console.WriteLine("  --dictionary-list");
        Console.WriteLine("  --dictionary-list --reveal");
        Console.WriteLine("  --dictionary-import terms.csv");
        Console.WriteLine("  --dictionary-remove id [id]...");
        Console.WriteLine("  --hotkey-show");
        Console.WriteLine("  --hotkey-set \"Ctrl+Shift+F9\"");
        Console.WriteLine("  --send-mode-show");
        Console.WriteLine("  --send-mode-enable");
        Console.WriteLine("  --send-mode-disable");
        Console.WriteLine("  --autostart-show");
        Console.WriteLine("  --autostart-enable");
        Console.WriteLine("  --autostart-disable");
        Console.WriteLine($"  --local-data-cleanup [{LocalDataCleanup.ConfirmationFlag}]");
        Console.WriteLine("  --policy-add-url-prefix prefix");
        Console.WriteLine("  --policy-add-regex type pattern");
        Console.WriteLine("  --policy-diagnostics");
        Console.WriteLine("  --policy-test \"text\" [--show-sanitized]");
        Console.WriteLine("  --rules-export directory");
        Console.WriteLine("  --audit-summary");
        Console.WriteLine("  --audit-view");
        Console.WriteLine("  --audit-verify");
        Console.WriteLine("  --audit-cleanup --keep count");
        Console.WriteLine("  --crash-reports");
        Console.WriteLine("  --project-workspace-protect workspace");
        Console.WriteLine("  --project-workspace-status workspace");
        Console.WriteLine("  --project-file-sanitize file [--protected-workspace workspace]");
        Console.WriteLine("  --project-file-smoke");
        Console.WriteLine("  --project-tool-output-sanitize workspace \"tool output\"");
        Console.WriteLine("  --project-tool-output-unmanaged workspace");
        Console.WriteLine("  --project-patch-dry-run file --protected-workspace workspace --source-content-hash hash --sanitized-edit \"text\"");
        Console.WriteLine("  --project-patch-apply file --protected-workspace workspace --source-content-hash hash --sanitized-edit \"text\" (--approve|--cancel)");
        Console.WriteLine("  --project-attachment-bypass-status workspace");
        Console.WriteLine("  --project-connector-bypass-status workspace");
        Console.WriteLine("  --project-file-product-smoke");
        Console.WriteLine("  --tray-app [--global]");
        Console.WriteLine("  --os-profiles-list");
        Console.WriteLine("  --os-compatibility-matrix");
        Console.WriteLine("  --os-surface-diagnostic");
        Console.WriteLine("  --os-composer-diagnostic");
        Console.WriteLine("  --os-composer-diagnostic-delay seconds");
        Console.WriteLine("  --os-demo-dry-run \"text\"");
        Console.WriteLine("  --os-demo-smoke");
        Console.WriteLine("  --product-smoke");
        Console.WriteLine("  --native-profiles-status");
        Console.WriteLine("  --native-profile-verify profile-id submit-binding newline-binding");
        Console.WriteLine("  --native-profile-verify-delay profile-id submit-binding newline-binding seconds");
        Console.WriteLine("  --native-submit-smoke");
        Console.WriteLine("  --os-demo-send-gate");
        Console.WriteLine("  --os-demo-local-target");
        Console.WriteLine("  --os-demo-hotkey");
        Console.WriteLine("  --os-demo-hotkey-apply");
        Console.WriteLine("  --os-demo-hotkey-send");
        Console.WriteLine("  --self-test");
    }
}
