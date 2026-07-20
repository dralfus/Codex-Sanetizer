using System.Collections.Generic;

namespace CodexRedactionGate;

public static class AttachmentIngestion
{
    public static ContentPart CreateTextAttachment(string id, string contentType, string text)
    {
        return new ContentPart(
            Id: id,
            ContentSource: ContentSources.TextAttachment,
            RawText: text,
            SourceMetadata: new Dictionary<string, string>
            {
                ["content_type"] = contentType,
                ["is_binary"] = "false"
            });
    }

    public static ContentPart CreateFileSnippet(string id, string fileName, string text)
    {
        return new ContentPart(
            Id: id,
            ContentSource: ContentSources.FileSnippet,
            RawText: text,
            SourceMetadata: new Dictionary<string, string>
            {
                ["file_name"] = fileName,
                ["is_binary"] = "false"
            });
    }

    public static ContentPart CreateUnsupportedBinaryMetadata(string id, string contentType)
    {
        return new ContentPart(
            Id: id,
            ContentSource: ContentSources.TextAttachment,
            RawText: string.Empty,
            SourceMetadata: new Dictionary<string, string>
            {
                ["content_type"] = contentType,
                ["is_binary"] = "true"
            });
    }
}
