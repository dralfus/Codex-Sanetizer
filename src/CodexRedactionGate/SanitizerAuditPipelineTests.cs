using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

[TestFixture]
[Category("sanitizer-pipeline-audit")]
public class SanitizerAuditPipelineTests
{
    [Test]
    public void ConfirmAudit_RecordsMetadataWithoutRawValues()
    {
        var request = CreatePromptRequest("Send SENSITIVE_MARKER");
        var replacement = new Replacement(
            "prompt",
            5,
            "SENSITIVE_MARKER".Length,
            SensitiveEntityTypes.SyntheticMarker,
            "SYNTHETIC_1234",
            SanitizerPipelineConstants.SyntheticAction,
            Restorable: false);

        var audit = new AuditEventBuilder().Create(
            request,
            SanitizeDecision.Confirm,
            new[]
            {
                new SanitizedEntity(
                    "prompt",
                    5,
                    "SENSITIVE_MARKER".Length,
                    SensitiveEntityTypes.SyntheticMarker,
                    SanitizerPipelineConstants.SyntheticDetectorId,
                    SanitizerPipelineConstants.SyntheticAction)
            },
            new[] { replacement },
            Array.Empty<Warning>(),
            elapsedMs: 10);

        Assert.That(audit.Decision, Is.EqualTo(SanitizeDecision.Confirm));
        Assert.That(audit.ReplacementSummaries.Single().Pseudonym, Is.EqualTo("SYNTHETIC_1234"));
        Assert.That(AuditInspection.Contains(audit, "SENSITIVE_MARKER"), Is.False);
        Assert.That(AuditInspection.Contains(audit, "Send SENSITIVE_MARKER"), Is.False);
    }

    private static SanitizeRequest CreatePromptRequest(string text)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart("prompt", ContentSources.PromptText, text, new Dictionary<string, string>())
            },
            Context: new SanitizationContext("tests", null, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none"));
    }
}
