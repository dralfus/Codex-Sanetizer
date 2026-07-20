using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal static class SanitizerPipelineConstants
{
    public const string BlockMarker = "BLOCK_THIS";
    public const string BlockAction = "block_synthetic";
    public const string BlockDetectorId = "synthetic_block_marker";
    public const string BlockWarningCode = "synthetic_block_marker";
    public const string SyntheticMarker = "SENSITIVE_MARKER";
    public const string SyntheticAction = "replace_synthetic";
    public const string SyntheticDetectorId = "synthetic_marker";
    public const string DictionaryDetectorId = "csv_dictionary";
    public const string TechnicalDetectorId = "technical_identifier";
}

internal sealed record SensitiveCandidate(
    string ContentPartId,
    int Offset,
    int Length,
    SensitiveEntityTypeId Type,
    SensitiveDetectorId DetectorId,
    SanitizerActionId Action,
    string OriginalValue,
    bool Restorable);

internal sealed record ContentText(string Text, IReadOnlyList<ContentPartSpan> Spans)
{
    public string ResolveContentPartId(int offset)
    {
        foreach (var span in Spans)
        {
            if (offset >= span.Start && offset < span.EndExclusive)
            {
                return span.Id;
            }
        }

        return Spans.Count == 1 ? Spans[0].Id : "combined";
    }
}

internal sealed record ContentPartSpan(string Id, int Start, int EndExclusive);

internal sealed record SanitizedOutputVerificationResult(bool Passed, string? ReasonCode);
