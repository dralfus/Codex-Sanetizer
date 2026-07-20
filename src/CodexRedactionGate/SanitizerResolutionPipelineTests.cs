using System;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

[TestFixture]
[Category("sanitizer-pipeline-resolution")]
public class SanitizerResolutionPipelineTests
{
    [Test]
    public void OverlappingFindings_PreferHigherRiskSpan()
    {
        var lowerRisk = new SensitiveCandidate(
            "combined",
            0,
            10,
            SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Domain),
            SensitiveDetectorId.From("domain"),
            SanitizerActionIds.PseudonymizeRestorable,
            "a.local",
            true);
        var higherRisk = new SensitiveCandidate(
            "combined",
            2,
            8,
            SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Token),
            SensitiveDetectorId.From("token"),
            SanitizerActionIds.RedactNonRestorable,
            "secret",
            false);

        var resolved = new SpanResolver().Resolve(new[] { lowerRisk, higherRisk });

        Assert.That(resolved.Single().Type, Is.EqualTo(SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Token)));
    }

    [Test]
    public void NonRestorableSecretReplacement_DoesNotUseVault()
    {
        var vault = new ThrowingMappingVault();
        var contentText = new ContentText("api_key=secret", new[] { new ContentPartSpan("prompt", 0, "api_key=secret".Length) });
        var replacements = new ReplacementPlanner(vault).Plan(contentText, new[]
        {
            new SensitiveCandidate(
                "combined",
                "api_key=".Length,
                "secret".Length,
                SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Token),
                SensitiveDetectorIds.SecretRegex,
                SanitizerActionIds.RedactNonRestorable,
                "secret",
                Restorable: false)
        });

        Assert.That(replacements.Single().Placeholder, Is.EqualTo("TOKEN_REDACTED"));
    }

    private sealed class ThrowingMappingVault : IMappingVault
    {
        public string GetOrCreatePseudonym(string entityType, string normalizedValue)
        {
            throw new InvalidOperationException("Vault should not be used for non-restorable secrets.");
        }

        public bool TryGetPseudonym(string entityType, string normalizedValue, out string pseudonym)
        {
            pseudonym = string.Empty;
            return false;
        }

        public bool TryGetOriginal(string pseudonym, out MappingVaultRecord record)
        {
            record = null!;
            return false;
        }
    }
}
