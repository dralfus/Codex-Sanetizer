using System.Linq;
using NUnit.Framework;
using CodexRedactionGate;

[TestFixture]
[Category("sanitizer-pipeline-rendering")]
public class SanitizerRenderingPipelineTests
{
    [Test]
    public void SanitizedText_RendersOrderedSpansInOnePass()
    {
        var rendered = new SanitizedTextRenderer().Render("A secret B internal C", new[]
        {
            new Replacement("prompt", 2, 6, SensitiveEntityTypes.Token, "TOKEN_REDACTED", PolicyActions.RedactNonRestorable, Restorable: false),
            new Replacement("prompt", 11, 8, SensitiveEntityTypes.Domain, "DOMAIN_1234", PolicyActions.PseudonymizeRestorable, Restorable: true)
        });

        Assert.That(rendered, Is.EqualTo("A TOKEN_REDACTED B DOMAIN_1234 C"));
    }

    [Test]
    public void SurvivingRawSpan_BlocksSanitizedOutput()
    {
        var replacement = new Replacement("prompt", 4, 6, SensitiveEntityTypes.Token, "TOKEN_REDACTED", PolicyActions.RedactNonRestorable, Restorable: false);

        var result = new SanitizedOutputVerifier().Verify(
            "key secret",
            "key secret",
            new[] { replacement },
            expectedReplacementCount: 1);

        Assert.That(result.Passed, Is.False);
        Assert.That(result.ReasonCode, Is.EqualTo("raw_span_survived"));
    }
}
