using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CodexRedactionGate;

public sealed record GitleaksFindingSpan(
    int Offset,
    int Length,
    string Type,
    string DetectorId,
    string RuleId);

public static class GitleaksFindingConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<GitleaksFindingSpan> Convert(string input, string findingsJson)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(findingsJson);

        var findings = JsonSerializer.Deserialize<List<GitleaksFindingDto>>(findingsJson, JsonOptions)
            ?? new List<GitleaksFindingDto>();
        var spans = new List<GitleaksFindingSpan>();

        foreach (var finding in findings)
        {
            var offset = GetOffset(input, finding.StartLine, finding.StartColumn);
            var endOffset = GetOffset(input, finding.EndLine, finding.EndColumn);
            if (offset < 0
                || endOffset < offset
                || endOffset >= input.Length)
            {
                continue;
            }

            spans.Add(new GitleaksFindingSpan(
                Offset: offset,
                Length: endOffset - offset + 1,
                Type: SensitiveEntityTypes.Secret,
                DetectorId: "gitleaks",
                RuleId: finding.RuleID ?? "unknown"));
        }

        return spans;
    }

    private static int GetOffset(string input, int line, int column)
    {
        if (line < 1 || column < 1)
        {
            return -1;
        }

        var currentLine = 1;
        var currentColumn = 1;

        for (var index = 0; index < input.Length; index++)
        {
            if (currentLine == line && currentColumn == column)
            {
                return index;
            }

            if (input[index] == '\r')
            {
                if (index + 1 < input.Length && input[index + 1] == '\n')
                {
                    index++;
                }

                currentLine++;
                currentColumn = 1;
                continue;
            }

            if (input[index] == '\n')
            {
                currentLine++;
                currentColumn = 1;
                continue;
            }

            currentColumn++;
        }

        return currentLine == line && currentColumn == column
            ? input.Length
            : -1;
    }

    private sealed record GitleaksFindingDto(
        string? RuleID,
        int StartLine,
        int EndLine,
        int StartColumn,
        int EndColumn,
        string? Secret,
        string? Match);
}
