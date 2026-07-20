using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexRedactionGate;

public sealed record PlainTextAttachmentOptions(long MaxBytes)
{
    public static PlainTextAttachmentOptions Default { get; } = new(MaxBytes: 1024 * 1024);
}

public sealed record AttachmentIntakeResult(
    bool Succeeded,
    string Code,
    ContentPart ContentPart,
    IReadOnlyList<Warning> Warnings);

public static class PlainTextAttachmentIntake
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".log",
        ".csv",
        ".json",
        ".md",
        ".cs",
        ".toml",
        ".yaml",
        ".yml",
        ".xml",
        ".ps1"
    };

    public static AttachmentIntakeResult ReadFile(
        string path,
        string contentPartId,
        PlainTextAttachmentOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPartId);

        var resolvedOptions = options ?? PlainTextAttachmentOptions.Default;
        var fileName = Path.GetFileName(path);
        var extension = Path.GetExtension(path);

        if (!SupportedExtensions.Contains(extension))
        {
            return Unsupported(contentPartId, "unsupported_attachment_type");
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return Unsupported(contentPartId, "attachment_unreadable");
            }

            if (fileInfo.Length > resolvedOptions.MaxBytes)
            {
                return Unsupported(contentPartId, "attachment_too_large");
            }

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var text = encoding.GetString(File.ReadAllBytes(path));
            return new AttachmentIntakeResult(
                Succeeded: true,
                Code: "attachment_text_loaded",
                ContentPart: AttachmentIngestion.CreateFileSnippet(contentPartId, fileName, text),
                Warnings: Array.Empty<Warning>());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Unsupported(contentPartId, "attachment_unreadable");
        }
    }

    private static AttachmentIntakeResult Unsupported(string contentPartId, string code)
    {
        return new AttachmentIntakeResult(
            Succeeded: false,
            Code: code,
            ContentPart: AttachmentIngestion.CreateUnsupportedBinaryMetadata(contentPartId, code),
            Warnings: new[]
            {
                new Warning(
                    Code: code,
                    Message: "Attachment could not be read as supported plain text.",
                    Severity: WarningSeverity.Error)
            });
    }
}
