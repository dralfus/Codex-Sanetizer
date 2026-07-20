using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

[TestFixture]
[Category("sanitizer-pipeline-detectors")]
public class SanitizerDetectorPipelineTests
{
    [Test]
    public void SyntheticAndDictionaryFindings_ReturnCommonCandidateShape()
    {
        var registry = DetectorRegistry.CreateDefault(
            new[]
            {
                new DictionaryTerm("customer", "ACME Banking", PolicyActions.PseudonymizeRestorable, null)
            },
            RedactionPolicy.BuiltInDefaults);

        var candidates = registry
            .DetectConfirmCandidates("Ask ACME Banking about SENSITIVE_MARKER", scannerResult: null)
            .ToArray();

        Assert.That(candidates.Select(candidate => candidate.Type.Value), Does.Contain("customer"));
        Assert.That(candidates.Select(candidate => candidate.Type.Value), Does.Contain(SensitiveEntityTypes.SyntheticMarker));
        Assert.That(candidates.All(candidate => candidate.ContentPartId == "combined"), Is.True);
    }

    [Test]
    public void GitleaksFindings_BecomeNonRestorableSecretCandidates()
    {
        const string text = "api_key=sk_live_1234567890abcdef";
        var secretOffset = text.IndexOf("sk_live", StringComparison.Ordinal);
        var scannerResult = new SecretScanResult(
            TimedOut: false,
            ScannerStatus: ScannerStatusIds.Findings.Value,
            Findings: new[]
            {
                new GitleaksFindingSpan(
                    Offset: secretOffset,
                    Length: "sk_live_1234567890abcdef".Length,
                    Type: SensitiveEntityTypes.Token,
                    DetectorId: "gitleaks",
                    RuleId: "test-rule")
            });

        var candidates = DetectorRegistry
            .CreateDefault(Array.Empty<DictionaryTerm>(), RedactionPolicy.BuiltInDefaults)
            .DetectConfirmCandidates(text, scannerResult);

        Assert.That(candidates.Single(candidate => candidate.DetectorId == SensitiveDetectorIds.Gitleaks).Restorable, Is.False);
        Assert.That(candidates.Single(candidate => candidate.DetectorId == SensitiveDetectorIds.Gitleaks).Action, Is.EqualTo(SanitizerActionIds.RedactNonRestorable));
    }

    [Test]
    public void InternalDecisionValues_RejectEmptyValues()
    {
        Assert.That(() => SensitiveEntityTypeId.From(string.Empty), Throws.TypeOf<ArgumentException>());
        Assert.That(() => SensitiveDetectorId.From(string.Empty), Throws.TypeOf<ArgumentException>());
        Assert.That(() => SanitizerActionId.From(string.Empty), Throws.TypeOf<ArgumentException>());
        Assert.That(() => ScannerStatusId.From(string.Empty), Throws.TypeOf<ArgumentException>());
    }
}
