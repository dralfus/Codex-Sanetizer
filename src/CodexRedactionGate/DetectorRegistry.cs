using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal interface ISensitiveDetector
{
    IReadOnlyList<SensitiveCandidate> Detect(string text, string contentPartId, SecretScanResult? scannerResult);
}

internal sealed class DetectorRegistry
{
    private readonly SyntheticDetector _syntheticDetector;
    private readonly IReadOnlyList<ISensitiveDetector> _confirmDetectors;

    private DetectorRegistry(
        SyntheticDetector syntheticDetector,
        IReadOnlyList<ISensitiveDetector> confirmDetectors)
    {
        _syntheticDetector = syntheticDetector;
        _confirmDetectors = confirmDetectors;
    }

    public static DetectorRegistry CreateDefault(
        IReadOnlyList<DictionaryTerm> dictionaryTerms,
        RedactionPolicy policy)
    {
        var allowlistEvaluator = new PublicAllowlistEvaluator(policy);
        var syntheticDetector = new SyntheticDetector();

        return new DetectorRegistry(
            syntheticDetector,
            new ISensitiveDetector[]
            {
                new TechnicalIdentifierDetector(allowlistEvaluator),
                new BuiltInSecretDetector(),
                new GitleaksFindingDetector(),
                new DictionaryDetector(dictionaryTerms),
                syntheticDetector
            });
    }

    public IReadOnlyList<SensitiveCandidate> DetectHardBlocks(string text)
    {
        return _syntheticDetector.DetectHardBlocks(text, "combined");
    }

    public IReadOnlyList<SensitiveCandidate> DetectConfirmCandidates(
        string text,
        SecretScanResult? scannerResult)
    {
        return _confirmDetectors
            .SelectMany(detector => detector.Detect(text, "combined", scannerResult))
            .ToArray();
    }
}
