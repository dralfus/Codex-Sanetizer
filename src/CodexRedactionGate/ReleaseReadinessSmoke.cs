using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record ReleaseReadinessSmokeReport(
    bool Passed,
    bool PolicyActivationAndPrecedencePassed,
    bool AuditChainVerificationPassed,
    bool ScannerPackageValidationPassed,
    bool AttachmentIntakePassed,
    bool GatewayHandoffPassed,
    bool RestorationHandoffPassed,
    bool OsAdapterDemoPassed);

public static class ReleaseReadinessSmokeRunner
{
    public static ReleaseReadinessSmokeReport Run(
        string workspaceDirectory,
        MvpPackageManifest manifest,
        byte[] hmacSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(hmacSecret);

        Directory.CreateDirectory(workspaceDirectory);

        var policyPassed = PolicyActivationAndPrecedencePass(Path.Combine(workspaceDirectory, "policy"));
        var auditPassed = AuditChainPass(Path.Combine(workspaceDirectory, "audit"), hmacSecret);
        var scannerPassed = ScannerRuntimeConfigurationValidator.Validate(manifest).Valid
            && MvpPackageSmokeRunner.Run(manifest, hmacSecret).ReleasePackageManifestSmokePassed;
        var attachmentPassed = AttachmentIntakePass(Path.Combine(workspaceDirectory, "attachments"), hmacSecret);
        var gatewayPassed = GatewayHandoffPass(hmacSecret);
        var restorationPassed = RestorationHandoffPass(hmacSecret);
        var osAdapterPassed = OsAdapterDemoRunner.RunSmoke(hmacSecret).Passed;

        return new ReleaseReadinessSmokeReport(
            Passed: policyPassed
                && auditPassed
                && scannerPassed
                && attachmentPassed
                && gatewayPassed
                && restorationPassed
                && osAdapterPassed,
            PolicyActivationAndPrecedencePassed: policyPassed,
            AuditChainVerificationPassed: auditPassed,
            ScannerPackageValidationPassed: scannerPassed,
            AttachmentIntakePassed: attachmentPassed,
            GatewayHandoffPassed: gatewayPassed,
            RestorationHandoffPassed: restorationPassed,
            OsAdapterDemoPassed: osAdapterPassed);
    }

    private static bool PolicyActivationAndPrecedencePass(string policyDirectory)
    {
        var activation = new PolicyActivationStore(policyDirectory)
            .PromoteCandidate("""
                version = 1
                profile = "release-smoke"

                [defaults]
                unknown_high_risk = "confirm"
                secret = "redact_non_restorable"
                internal_identifier = "pseudonymize_restorable"

                [scanners]
                gitleaks_enabled = true
                gitleaks_timeout_ms = 5000
                """);
        var precedence = PolicyPrecedenceReporter.Build(new[]
        {
            new PolicySource("global", RedactionPolicy.BuiltInDefaults),
            new PolicySource("project", activation.ActivePolicy)
        });

        return activation.Activated
            && precedence.SourcePrecedence.SequenceEqual(new[] { "global", "project" })
            && precedence.WinningSourceByArea["conflicts"] == "last_source_wins";
    }

    private static bool AuditChainPass(string auditDirectory, byte[] hmacSecret)
    {
        var sanitizer = new Sanitizer(
            new InMemoryHmacMappingVault(hmacSecret),
            Array.Empty<DictionaryTerm>(),
            RedactionPolicy.BuiltInDefaults,
            secretScanner: null,
            auditSink: new FileAuditSink(auditDirectory));

        sanitizer.Sanitize(CreateRequest("Normal prompt text"));
        sanitizer.Sanitize(CreateRequest("Connect to 192.168.10.25"));

        return AuditChainVerifier.Verify(auditDirectory).Valid;
    }

    private static bool AttachmentIntakePass(string attachmentDirectory, byte[] hmacSecret)
    {
        Directory.CreateDirectory(attachmentDirectory);
        var path = Path.Combine(attachmentDirectory, "config.txt");
        File.WriteAllText(path, "api_key=sk_live_1234567890abcdef");
        var intake = PlainTextAttachmentIntake.ReadFile(path, "release-attachment");
        var sanitizer = new Sanitizer(new InMemoryHmacMappingVault(hmacSecret));
        var result = sanitizer.Sanitize(new SanitizeRequest(
            ContentParts: new[] { intake.ContentPart },
            Context: new SanitizationContext("release-smoke", null, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none")));

        return intake.Succeeded
            && result.Decision == SanitizeDecision.Confirm
            && !result.SanitizedText.Contains("sk_live_1234567890abcdef", StringComparison.Ordinal);
    }

    private static bool GatewayHandoffPass(byte[] hmacSecret)
    {
        var approvedSubmitter = new SmokePromptSubmitter();
        var approvedComposer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
            new SubmitOwningAdapter(approvedSubmitter, new SmokeConfirmationProvider(ConfirmationDecisionContract.Confirm)));
        var canceledSubmitter = new SmokePromptSubmitter();
        var canceledComposer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
            new SubmitOwningAdapter(canceledSubmitter, new SmokeConfirmationProvider(ConfirmationDecisionContract.Cancel)));
        var blockedSubmitter = new SmokePromptSubmitter();
        var blockedComposer = new LocalComposerShell(
            new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
            new SubmitOwningAdapter(blockedSubmitter, new SmokeConfirmationProvider(null)));

        var approved = approvedComposer.Submit("Connect to 192.168.10.25");
        var canceled = canceledComposer.Submit("Connect to 192.168.10.25");
        var blocked = blockedComposer.Submit("Reject BLOCK_THIS");

        return approved.SubmitOutcome.Submitted
            && approvedSubmitter.SubmittedTexts.Single().Contains("IP_", StringComparison.Ordinal)
            && !approvedSubmitter.SubmittedTexts.Single().Contains("192.168.10.25", StringComparison.Ordinal)
            && !canceled.SubmitOutcome.Submitted
            && canceledSubmitter.SubmittedTexts.Count == 0
            && !blocked.SubmitOutcome.Submitted
            && blockedSubmitter.SubmittedTexts.Count == 0;
    }

    private static bool RestorationHandoffPass(byte[] hmacSecret)
    {
        var vault = new InMemoryHmacMappingVault(hmacSecret);
        var pseudonym = vault.GetOrCreatePseudonym("ip_address", "192.168.10.25");
        var result = RestorationHandoff.RestoreAndEvaluate(
            new LocalRestorer(vault),
            new RestoreRequest(
                SanitizedText: $"Connect to {pseudonym}",
                Replacements: new[]
                {
                    new Replacement("prompt", 11, pseudonym.Length, "ip_address", pseudonym, PolicyActions.PseudonymizeRestorable, Restorable: true)
                }));

        return result.Restoration.Metadata.LocalSensitive
            && result.Restoration.Text.Contains("192.168.10.25", StringComparison.Ordinal)
            && !result.SubmitDecision.CanSubmit;
    }

    private static SanitizeRequest CreateRequest(string text)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart("prompt", ContentSources.PromptText, text, new Dictionary<string, string>())
            },
            Context: new SanitizationContext("release-smoke", null, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none"));
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
        private readonly Func<ConfirmationUiModel, ConfirmationDecision>? _decisionFactory;

        public SmokeConfirmationProvider(Func<ConfirmationUiModel, ConfirmationDecision>? decisionFactory)
        {
            _decisionFactory = decisionFactory;
        }

        public ConfirmationDecision RequestConfirmation(ConfirmationUiModel model)
        {
            return _decisionFactory?.Invoke(model) ?? ConfirmationDecisionContract.Cancel(model);
        }
    }
}
