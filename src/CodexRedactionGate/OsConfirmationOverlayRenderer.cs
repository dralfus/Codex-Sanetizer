using System;
using System.Linq;
using System.Text;

namespace CodexRedactionGate;

public static class OsConfirmationOverlayRenderer
{
    public static string RenderText(ConfirmationUiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var builder = new StringBuilder();
        builder.AppendLine("Codex Redaction Gate");
        builder.AppendLine(model.PrimaryAction);
        builder.AppendLine(model.SecondaryAction);
        builder.AppendLine($"raw_values_visible: {model.RawValuesVisible.ToString().ToLowerInvariant()}");
        builder.AppendLine("counts:");
        foreach (var item in model.CountsByType.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"{item.Key}: {item.Value}");
        }

        builder.AppendLine("warnings:");
        foreach (var warning in model.HighRiskWarnings)
        {
            builder.AppendLine(warning);
        }

        builder.AppendLine("sanitized_prompt:");
        builder.AppendLine(Highlight(model));
        return builder.ToString();
    }

    private static string Highlight(ConfirmationUiModel model)
    {
        if (model.HighlightedSpans.Count == 0)
        {
            return model.SanitizedPrompt;
        }

        var builder = new StringBuilder();
        var cursor = 0;
        foreach (var span in model.HighlightedSpans.OrderBy(span => span.Offset))
        {
            if (span.Offset > cursor)
            {
                builder.Append(model.SanitizedPrompt.Substring(cursor, span.Offset - cursor));
            }

            builder.Append("[[");
            builder.Append(model.SanitizedPrompt.Substring(span.Offset, span.Length));
            builder.Append(':');
            builder.Append(span.Type);
            builder.Append("]]");
            cursor = span.Offset + span.Length;
        }

        if (cursor < model.SanitizedPrompt.Length)
        {
            builder.Append(model.SanitizedPrompt.Substring(cursor));
        }

        return builder.ToString();
    }
}
