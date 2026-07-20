using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class SyntheticDetector : ISensitiveDetector
{
    public IReadOnlyList<SensitiveCandidate> DetectHardBlocks(string text, string contentPartId)
    {
        return TextSpanUtilities.FindOffsets(text, SanitizerPipelineConstants.BlockMarker)
            .Select(offset => new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: offset,
                Length: SanitizerPipelineConstants.BlockMarker.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.SyntheticBlockMarker),
                DetectorId: SensitiveDetectorIds.SyntheticBlock,
                Action: SanitizerActionIds.BlockSynthetic,
                OriginalValue: SanitizerPipelineConstants.BlockMarker,
                Restorable: false))
            .ToArray();
    }

    public IReadOnlyList<SensitiveCandidate> Detect(
        string text,
        string contentPartId,
        SecretScanResult? scannerResult)
    {
        return TextSpanUtilities.FindOffsets(text, SanitizerPipelineConstants.SyntheticMarker)
            .Select(offset => new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: offset,
                Length: SanitizerPipelineConstants.SyntheticMarker.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.SyntheticMarker),
                DetectorId: SensitiveDetectorIds.SyntheticMarker,
                Action: SanitizerActionIds.ReplaceSynthetic,
                OriginalValue: SanitizerPipelineConstants.SyntheticMarker,
                Restorable: false))
            .ToArray();
    }
}

internal sealed class DictionaryDetector : ISensitiveDetector
{
    private readonly IReadOnlyList<DictionaryTerm> _dictionaryTerms;

    public DictionaryDetector(IReadOnlyList<DictionaryTerm> dictionaryTerms)
    {
        _dictionaryTerms = dictionaryTerms;
    }

    public IReadOnlyList<SensitiveCandidate> Detect(
        string text,
        string contentPartId,
        SecretScanResult? scannerResult)
    {
        var candidates = new List<SensitiveCandidate>();

        foreach (var term in _dictionaryTerms)
        {
            foreach (var offset in FindTermOffsets(text, term))
            {
                candidates.Add(new SensitiveCandidate(
                    ContentPartId: contentPartId,
                    Offset: offset,
                    Length: term.Value.Length,
                    Type: SensitiveEntityTypeIds.FromPublic(term.Type),
                    DetectorId: SensitiveDetectorIds.Dictionary,
                    Action: SanitizerActionIds.FromPublic(term.Action),
                    OriginalValue: term.Value,
                    Restorable: true));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Offset)
            .ThenByDescending(candidate => candidate.Length)
            .ToArray();
    }

    private static IReadOnlyList<int> FindTermOffsets(string text, DictionaryTerm term)
    {
        return TextSpanUtilities.FindOffsetsForEntity(text, term.Value, term.Type);
    }
}
