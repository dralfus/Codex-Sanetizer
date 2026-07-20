using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class ContentPartAssembler
{
    public ContentText Assemble(IReadOnlyList<ContentPart> contentParts)
    {
        var text = string.Concat(contentParts.Select(part => part.RawText));
        var spans = new List<ContentPartSpan>();
        var offset = 0;

        foreach (var part in contentParts)
        {
            spans.Add(new ContentPartSpan(part.Id, offset, offset + part.RawText.Length));
            offset += part.RawText.Length;
        }

        return new ContentText(text, spans);
    }
}

internal sealed class AttachmentGuard
{
    public string? FindUnsupportedBinaryAttachmentId(IReadOnlyList<ContentPart> contentParts)
    {
        foreach (var part in contentParts)
        {
            if (IsUnsupportedBinaryAttachment(part))
            {
                return part.Id;
            }
        }

        return null;
    }

    private static bool IsUnsupportedBinaryAttachment(ContentPart part)
    {
        if (part.SourceMetadata.TryGetValue("is_binary", out var isBinary)
            && string.Equals(isBinary, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!part.SourceMetadata.TryGetValue("content_type", out var contentType))
        {
            return false;
        }

        return !IsSupportedTextContentType(contentType);
    }

    private static bool IsSupportedTextContentType(string contentType)
    {
        var normalized = contentType.Split(';')[0].Trim().ToLowerInvariant();

        return normalized.StartsWith("text/", StringComparison.Ordinal)
            || normalized is "application/json"
                or "application/xml"
                or "application/yaml"
                or "application/x-yaml"
                or "application/toml"
                or "application/csv"
                or "application/javascript";
    }
}
