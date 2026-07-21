using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public interface ISanitizer
{
    SanitizationResult Sanitize(SanitizeRequest request);
}

public sealed class Sanitizer : ISanitizer
{
    public static TimeSpan TotalHardCap { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan GitleaksBudgetCap { get; } = TimeSpan.FromSeconds(5);

    private readonly ContentPartAssembler _contentPartAssembler;
    private readonly AttachmentGuard _attachmentGuard;
    private readonly PolicyBlockEvaluator _policyBlockEvaluator;
    private readonly ExternalScannerOrchestrator _scannerOrchestrator;
    private readonly DetectorRegistry _detectorRegistry;
    private readonly SpanResolver _spanResolver;
    private readonly ReplacementPlanner _replacementPlanner;
    private readonly SanitizedTextRenderer _renderer;
    private readonly SanitizedOutputVerifier _verifier;
    private readonly SanitizationResultAssembler _resultAssembler;

    public Sanitizer()
        : this(DefaultStorageLayout.CreateDefault())
    {
    }

    private Sanitizer(DefaultStorageLayout layout)
        : this(
            CreateProductionVault(layout),
            new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout)).LoadTerms(),
            LoadProductionPolicy(layout).ActivePolicy)
    {
    }

    public Sanitizer(IMappingVault mappingVault)
        : this(mappingVault, Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults)
    {
    }

    public Sanitizer(IMappingVault mappingVault, IReadOnlyList<DictionaryTerm> dictionaryTerms)
        : this(mappingVault, dictionaryTerms, RedactionPolicy.BuiltInDefaults)
    {
    }

    public Sanitizer(
        IMappingVault mappingVault,
        IReadOnlyList<DictionaryTerm> dictionaryTerms,
        RedactionPolicy policy)
        : this(mappingVault, dictionaryTerms, policy, null)
    {
    }

    public Sanitizer(
        IMappingVault mappingVault,
        IReadOnlyList<DictionaryTerm> dictionaryTerms,
        RedactionPolicy policy,
        ISecretScanner? secretScanner,
        IAuditSink? auditSink = null)
    {
        ArgumentNullException.ThrowIfNull(mappingVault);
        ArgumentNullException.ThrowIfNull(dictionaryTerms);
        ArgumentNullException.ThrowIfNull(policy);

        _contentPartAssembler = new ContentPartAssembler();
        _attachmentGuard = new AttachmentGuard();
        _policyBlockEvaluator = new PolicyBlockEvaluator(policy);
        _scannerOrchestrator = new ExternalScannerOrchestrator(secretScanner, policy);
        _detectorRegistry = DetectorRegistry.CreateDefault(dictionaryTerms, policy);
        _spanResolver = new SpanResolver();
        _replacementPlanner = new ReplacementPlanner(mappingVault);
        _renderer = new SanitizedTextRenderer();
        _verifier = new SanitizedOutputVerifier();
        _resultAssembler = new SanitizationResultAssembler(auditSink);
    }

    public static Sanitizer CreateProduction(IReadOnlyList<DictionaryTerm> dictionaryTerms)
    {
        var layout = DefaultStorageLayout.CreateDefault();
        var managedTerms = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout)).LoadTerms();
        var managedPolicy = LoadProductionPolicy(layout);
        return new Sanitizer(
            CreateProductionVault(layout),
            managedTerms.Concat(dictionaryTerms).ToArray(),
            managedPolicy.ActivePolicy,
            CreateProductionSecretScanner());
    }

    internal static Sanitizer CreateProduction(ManagedPolicyLoadResult managedPolicy)
    {
        ArgumentNullException.ThrowIfNull(managedPolicy);

        var layout = DefaultStorageLayout.CreateDefault();
        var managedTerms = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout)).LoadTerms();
        return new Sanitizer(
            CreateProductionVault(layout),
            managedTerms,
            managedPolicy.ActivePolicy,
            CreateProductionSecretScanner());
    }

    public static Sanitizer CreateProduction(
        DefaultStorageLayout layout,
        IReadOnlyList<DictionaryTerm>? dictionaryTerms = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var managedTerms = new ManagedSensitiveDictionary(ManagedSensitiveDictionary.DefaultPath(layout)).LoadTerms();
        var managedPolicy = LoadProductionPolicy(layout);
        return new Sanitizer(
            CreateProductionVault(layout),
            managedTerms.Concat(dictionaryTerms ?? Array.Empty<DictionaryTerm>()).ToArray(),
            managedPolicy.ActivePolicy,
            CreateProductionSecretScanner());
    }

    internal static ManagedPolicyLoadResult LoadProductionPolicy(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new PolicyActivationStore(layout.PolicyDirectory).LoadActivePolicyOrDefault();
    }

    public SanitizationResult Sanitize(SanitizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var trace = new SanitizerDecisionTrace();
        var unsupportedBinaryAttachmentId = _attachmentGuard.FindUnsupportedBinaryAttachmentId(request.ContentParts);
        if (unsupportedBinaryAttachmentId is not null)
        {
            stopwatch.Stop();
            trace.MarkAttachments("blocked");
            trace.SetReason("unsupported_binary_attachment");
            return _resultAssembler.Block(
                request,
                Array.Empty<SanitizedEntity>(),
                Array.Empty<Replacement>(),
                new[]
                {
                    new Warning(
                        Code: "unsupported_binary_attachment",
                        Message: $"Unsupported binary attachment blocked: {unsupportedBinaryAttachmentId}.",
                        Severity: WarningSeverity.Error)
                },
                stopwatch.ElapsedMilliseconds,
                trace.ToScannerStatuses());
        }

        trace.MarkAttachments("allowed");
        var contentText = _contentPartAssembler.Assemble(request.ContentParts);
        var hardBlockCandidates = _detectorRegistry.DetectHardBlocks(contentText.Text);
        if (hardBlockCandidates.Count > 0)
        {
            stopwatch.Stop();
            trace.MarkDetectors("hard_block_candidates");
            trace.AddCandidateCounts(hardBlockCandidates);
            trace.SetReason(SanitizerPipelineConstants.BlockWarningCode);
            var hardBlockEntities = SanitizationResultAssembler.CreateEntities(contentText, hardBlockCandidates);
            return _resultAssembler.Block(
                request,
                hardBlockEntities,
                Array.Empty<Replacement>(),
                new[]
                {
                    new Warning(
                        Code: SanitizerPipelineConstants.BlockWarningCode,
                        Message: "Synthetic hard-block marker detected.",
                        Severity: WarningSeverity.Error)
                },
                stopwatch.ElapsedMilliseconds,
                trace.ToScannerStatuses());
        }

        var policyBlockRule = _policyBlockEvaluator.FindMatchingBlockRule(contentText.Text);
        if (policyBlockRule is not null)
        {
            stopwatch.Stop();
            trace.MarkPolicy("blocked");
            trace.SetReason("policy_block_rule");
            return _resultAssembler.Block(
                request,
                Array.Empty<SanitizedEntity>(),
                Array.Empty<Replacement>(),
                new[]
                {
                    new Warning(
                        Code: "policy_block_rule",
                        Message: "Prompt blocked by policy block rule.",
                        Severity: WarningSeverity.Error)
                },
                stopwatch.ElapsedMilliseconds,
                trace.ToScannerStatuses());
        }

        trace.MarkPolicy("no_block");
        var scannerResult = _scannerOrchestrator.Run(contentText.Text, stopwatch);
        trace.MarkScanner(scannerResult?.ScannerStatus ?? "not_configured");
        if (scannerResult is not null && ExternalScannerOrchestrator.IsFatal(scannerResult))
        {
            stopwatch.Stop();
            var warningCode = scannerResult.TimedOut ? "scanner_timeout" : "scanner_error";
            trace.SetReason(warningCode);
            return _resultAssembler.Block(
                request,
                Array.Empty<SanitizedEntity>(),
                Array.Empty<Replacement>(),
                new[]
                {
                    new Warning(
                        Code: warningCode,
                        Message: "External secret scanner failed closed.",
                        Severity: WarningSeverity.Error)
                },
                stopwatch.ElapsedMilliseconds,
                trace.ToScannerStatuses(new Dictionary<string, string> { ["gitleaks"] = scannerResult.ScannerStatus }));
        }

        var candidates = _detectorRegistry.DetectConfirmCandidates(contentText.Text, scannerResult);
        var finalCandidates = _spanResolver.Resolve(candidates).ToArray();
        trace.AddCandidateCounts(finalCandidates);
        if (finalCandidates.Length == 0)
        {
            stopwatch.Stop();
            trace.MarkDetectors("no_candidates");
            trace.SetReason("no_sensitive_candidates");
            return _resultAssembler.Allow(
                request,
                contentText.Text,
                stopwatch.ElapsedMilliseconds,
                trace.ToScannerStatuses());
        }

        stopwatch.Stop();
        trace.MarkDetectors("candidates_found");
        var replacements = _replacementPlanner.Plan(contentText, finalCandidates);
        var entities = SanitizationResultAssembler.CreateEntities(contentText, finalCandidates);
        var renderedText = _renderer.Render(contentText.Text, replacements);
        var verification = _verifier.Verify(contentText.Text, renderedText, replacements, finalCandidates.Length);

        if (!verification.Passed)
        {
            trace.MarkVerification(verification.ReasonCode ?? "failed");
            trace.SetReason("sanitized_output_verification_failed");
            return _resultAssembler.Block(
                request,
                entities,
                Array.Empty<Replacement>(),
                new[]
                {
                    new Warning(
                        Code: "sanitized_output_verification_failed",
                        Message: $"Sanitized output verification failed: {verification.ReasonCode}.",
                        Severity: WarningSeverity.Error)
                },
                stopwatch.ElapsedMilliseconds,
                trace.ToScannerStatuses());
        }

        trace.MarkVerification("passed");
        trace.SetReason("sensitive_candidates_found");
        return _resultAssembler.Confirm(
            request,
            renderedText,
            entities,
            replacements,
            stopwatch.ElapsedMilliseconds,
            trace.ToScannerStatuses());
    }

    internal static SanitizedOutputVerificationResult VerifySanitizedOutput(
        string originalText,
        string sanitizedText,
        IReadOnlyList<Replacement> replacements,
        int expectedReplacementCount)
    {
        return new SanitizedOutputVerifier().Verify(
            originalText,
            sanitizedText,
            replacements,
            expectedReplacementCount);
    }

    private static IMappingVault CreateProductionVault(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(layout.RootDirectory);
        Directory.CreateDirectory(layout.VaultDirectory);
        FileMappingVault.MigrateLegacyDefaultVaultIfNeeded(layout);

        var secretProvider = new DpapiProtectedHmacSecretProvider(
            Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName),
            new WindowsDpapiDataProtector());
        return FileMappingVault.CreateProtected(
            Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName),
            secretProvider.GetOrCreateSecret(),
            new WindowsDpapiDataProtector());
    }

    private static ISecretScanner? CreateProductionSecretScanner()
    {
        var scannerPackage = ScannerPackageManifestResolver.ResolveDefault(AppContext.BaseDirectory);
        if (scannerPackage.Report.SafeDisabled)
        {
            return null;
        }

        return new ScannerConfigurationGuardedSecretScanner(
            new GitleaksSecretScanner(new GitleaksPipeAdapter(), scannerPackage.Manifest.GitleaksBinaryPath),
            () => ScannerPackageManifestResolver.ResolveDefault(AppContext.BaseDirectory).Report);
    }

}
