using System.Collections.Generic;
using NUnit.Framework;
using CodexRedactionGate;

[TestFixture]
[Category("sanitizer-pipeline-content")]
public class SanitizerContentPipelineTests
{
    [Test]
    public void TextParts_KeepsSourcePartOffsetMapping()
    {
        var contentText = new ContentPartAssembler().Assemble(new[]
        {
            new ContentPart("prompt", ContentSources.PromptText, "Check ", new Dictionary<string, string>()),
            new ContentPart("attachment", ContentSources.TextAttachment, "SENSITIVE_MARKER", new Dictionary<string, string>())
        });

        Assert.That(contentText.Text, Is.EqualTo("Check SENSITIVE_MARKER"));
        Assert.That(contentText.ResolveContentPartId("Check ".Length), Is.EqualTo("attachment"));
    }

    [Test]
    public void UnsupportedBinaryAttachmentMetadata_IsBlocked()
    {
        var blockedPartId = new AttachmentGuard().FindUnsupportedBinaryAttachmentId(new[]
        {
            new ContentPart(
                "archive",
                ContentSources.TextAttachment,
                "not inspected",
                new Dictionary<string, string> { ["is_binary"] = "true" })
        });

        Assert.That(blockedPartId, Is.EqualTo("archive"));
    }
}
