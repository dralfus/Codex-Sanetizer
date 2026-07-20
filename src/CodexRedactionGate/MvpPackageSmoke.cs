using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record MvpPackageManifest(
    string AppArtifactPath,
    string GitleaksBinaryPath,
    string GitleaksProvenancePath);

public sealed record MvpPackageSmokeReport(
    bool DotNetAppArtifactPresent,
    string GitleaksBinaryPath,
    bool GitleaksBinaryPresent,
    bool GitleaksProvenanceLoaded,
    bool RequiresGit,
    bool RequiresGo,
    bool RequiresGitleaksSourceCode,
    bool RequiresNetwork,
    bool SanitizeAllowPassed,
    bool ConfirmPassed,
    bool GuardBlockPassed,
    bool LocalRestorePassed,
    bool ScannerArtifactSmokePassed,
    bool ScannerConfigurationValid,
    bool ScannerChecksumMatched,
    string? ScannerConfigurationWarningCode,
    bool ConfirmHandoffSmokePassed,
    bool AttachmentIngestionSmokePassed,
    bool ReleasePackageManifestSmokePassed);

public static class MvpPackageSmokeRunner
{
    public static MvpPackageSmokeReport Run(MvpPackageManifest manifest, byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(hmacSecret);

        var scannerConfiguration = ScannerRuntimeConfigurationValidator.Validate(manifest);
        var vault = new InMemoryHmacMappingVault(hmacSecret);
        var sanitizer = new Sanitizer(vault);
        var allowResult = sanitizer.Sanitize(CreateRequest("Normal prompt text"));
        var confirmResult = sanitizer.Sanitize(CreateRequest("Connect to 192.168.10.25"));
        var guardDecision = new GuardHookShell(sanitizer).Evaluate(CreateRequest("Reject BLOCK_THIS"));
        var restorePassed = RestoreRoundTrip(vault, confirmResult);
        var scannerArtifactSmokePassed = scannerConfiguration.Valid && ScannerBackedPathPasses(hmacSecret);
        var confirmHandoffSmokePassed = ConfirmHandoffPasses(confirmResult);
        var attachmentIngestionSmokePassed = AttachmentIngestionPasses(hmacSecret);

        return new MvpPackageSmokeReport(
            DotNetAppArtifactPresent: File.Exists(manifest.AppArtifactPath),
            GitleaksBinaryPath: manifest.GitleaksBinaryPath,
            GitleaksBinaryPresent: scannerConfiguration.BinaryPresent,
            GitleaksProvenanceLoaded: scannerConfiguration.ProvenanceLoaded,
            RequiresGit: scannerConfiguration.RequiresGit,
            RequiresGo: scannerConfiguration.RequiresGo,
            RequiresGitleaksSourceCode: scannerConfiguration.RequiresGitleaksSourceCode,
            RequiresNetwork: scannerConfiguration.RequiresNetwork,
            SanitizeAllowPassed: allowResult.Decision == SanitizeDecision.Allow,
            ConfirmPassed: confirmResult.Decision == SanitizeDecision.Confirm
                && !confirmResult.SanitizedText.Contains("192.168.10.25", StringComparison.Ordinal),
            GuardBlockPassed: !guardDecision.PermitOriginalPrompt
                && guardDecision.SanitizationResult.Decision == SanitizeDecision.Block,
            LocalRestorePassed: restorePassed,
            ScannerArtifactSmokePassed: scannerArtifactSmokePassed,
            ScannerConfigurationValid: scannerConfiguration.Valid,
            ScannerChecksumMatched: scannerConfiguration.BinaryChecksumMatches,
            ScannerConfigurationWarningCode: scannerConfiguration.WarningCode,
            ConfirmHandoffSmokePassed: confirmHandoffSmokePassed,
            AttachmentIngestionSmokePassed: attachmentIngestionSmokePassed,
            ReleasePackageManifestSmokePassed: scannerConfiguration.Valid
                && File.Exists(manifest.AppArtifactPath)
                && !scannerConfiguration.RequiresGit
                && !scannerConfiguration.RequiresGo
                && !scannerConfiguration.RequiresNetwork);
    }

    private static bool RestoreRoundTrip(IMappingVault vault, SanitizationResult confirmResult)
    {
        var restored = new LocalRestorer(vault).Restore(new RestoreRequest(
            SanitizedText: confirmResult.SanitizedText,
            Replacements: confirmResult.Replacements));

        return restored.Metadata.LocalSensitive
            && restored.Text.Contains("192.168.10.25", StringComparison.Ordinal);
    }

    private static bool ScannerBackedPathPasses(byte[] hmacSecret)
    {
        var input = "key=abcdef";
        var scanner = new SmokeSecretScanner(new SecretScanResult(
            TimedOut: false,
            ScannerStatus: "findings",
            Findings: new[]
            {
                new GitleaksFindingSpan(
                    Offset: input.IndexOf("abcdef", StringComparison.Ordinal),
                    Length: "abcdef".Length,
                    Type: SensitiveEntityTypes.Secret,
                    DetectorId: "gitleaks",
                    RuleId: "smoke")
            }));
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(hmacSecret),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicy.BuiltInDefaults,
            scanner);
        var result = sanitizer.Sanitize(CreateRequest(input));

        return result.Decision == SanitizeDecision.Confirm
            && result.SanitizedText == "key=SECRET_REDACTED"
            && scanner.WasCalled;
    }

    private static bool ConfirmHandoffPasses(SanitizationResult confirmResult)
    {
        var submitter = new SmokePromptSubmitter();
        var adapter = new SubmitOwningAdapter(
            submitter,
            new SmokeConfirmationProvider(ConfirmationDecisionContract.Confirm));

        var outcome = adapter.Handle(confirmResult);

        return outcome.Submitted
            && submitter.SubmittedTexts.Count == 1
            && string.Equals(submitter.SubmittedTexts[0], confirmResult.SanitizedText, StringComparison.Ordinal)
            && !submitter.SubmittedTexts[0].Contains("192.168.10.25", StringComparison.Ordinal);
    }

    private static bool AttachmentIngestionPasses(byte[] hmacSecret)
    {
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(hmacSecret));
        var result = sanitizer.Sanitize(new SanitizeRequest(
            ContentParts: new[]
            {
                AttachmentIngestion.CreateTextAttachment("attachment", "text/plain", "api_key=sk_live_1234567890abcdef"),
                new ContentPart("prompt", ContentSources.PromptText, " ", new Dictionary<string, string>()),
                AttachmentIngestion.CreateFileSnippet("snippet", "config.txt", "password=P@ssw0rd!")
            },
            Context: new SanitizationContext(
                Application: "package-smoke",
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: false,
                AllowSecretStorage: false,
                ConfirmationMode: "none")));

        return result.Decision == SanitizeDecision.Confirm
            && result.Replacements.Select(replacement => replacement.ContentPartId).ToHashSet().SetEquals(new[] { "attachment", "snippet" })
            && !result.SanitizedText.Contains("sk_live_1234567890abcdef", StringComparison.Ordinal)
            && !result.SanitizedText.Contains("P@ssw0rd!", StringComparison.Ordinal);
    }

    private static SanitizeRequest CreateRequest(string text)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart("prompt", ContentSources.PromptText, text, new Dictionary<string, string>())
            },
            Context: new SanitizationContext(
                Application: "package-smoke",
                WorkspacePath: null,
                ProjectId: null,
                SessionId: null,
                PolicyProfile: "default"),
            Options: new SanitizationOptions(
                AllowSessionAliases: false,
                AllowSecretStorage: false,
                ConfirmationMode: "none"));
    }

    private sealed class SmokeSecretScanner : ISecretScanner
    {
        private readonly SecretScanResult _result;

        public SmokeSecretScanner(SecretScanResult result)
        {
            _result = result;
        }

        public bool WasCalled { get; private set; }

        public SecretScanResult Scan(string input, TimeSpan timeout)
        {
            WasCalled = true;
            return _result;
        }
    }

    private sealed class SmokePromptSubmitter : IPromptSubmitter
    {
        public List<string> SubmittedTexts { get; } = new();

        public void Submit(string text)
        {
            SubmittedTexts.Add(text);
        }
    }

    private sealed class SmokeConfirmationProvider : IConfirmationProvider
    {
        private readonly Func<ConfirmationUiModel, ConfirmationDecision> _decisionFactory;

        public SmokeConfirmationProvider(Func<ConfirmationUiModel, ConfirmationDecision> decisionFactory)
        {
            _decisionFactory = decisionFactory;
        }

        public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
        {
            return _decisionFactory(model);
        }
    }
}
