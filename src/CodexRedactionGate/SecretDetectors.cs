using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class BuiltInSecretDetector : ISensitiveDetector
{
    public IReadOnlyList<SensitiveCandidate> Detect(
        string text,
        string contentPartId,
        SecretScanResult? scannerResult)
    {
        var candidates = new List<SensitiveCandidate>();

        foreach (Match match in SanitizerRegexes.PrivateKey.Matches(text))
        {
            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: match.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.PrivateKey),
                DetectorId: SensitiveDetectorIds.SecretRegex,
                Action: SanitizerActionIds.RedactNonRestorable,
                OriginalValue: match.Value,
                Restorable: false));
        }

        foreach (Match match in SanitizerRegexes.TokenValue.Matches(text))
        {
            var value = match.Groups[1].Value;

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Groups[1].Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Token),
                DetectorId: SensitiveDetectorIds.SecretRegex,
                Action: SanitizerActionIds.RedactNonRestorable,
                OriginalValue: value,
                Restorable: false));
        }

        foreach (Match match in SanitizerRegexes.PasswordValue.Matches(text))
        {
            var value = match.Groups[1].Value;

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Groups[1].Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Password),
                DetectorId: SensitiveDetectorIds.SecretRegex,
                Action: SanitizerActionIds.RedactNonRestorable,
                OriginalValue: value,
                Restorable: false));
        }

        return candidates
            .OrderBy(candidate => candidate.Offset)
            .ThenByDescending(candidate => candidate.Length)
            .ToArray();
    }
}

internal sealed class GitleaksFindingDetector : ISensitiveDetector
{
    public IReadOnlyList<SensitiveCandidate> Detect(
        string text,
        string contentPartId,
        SecretScanResult? scannerResult)
    {
        if (scannerResult is null || scannerResult.Findings.Count == 0)
        {
            return Array.Empty<SensitiveCandidate>();
        }

        return scannerResult.Findings
            .Where(finding => finding.Offset >= 0 && finding.Length > 0 && finding.Offset + finding.Length <= text.Length)
            .Select(finding => new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: finding.Offset,
                Length: finding.Length,
                Type: SensitiveEntityTypeIds.FromPublic(finding.Type),
                DetectorId: SensitiveDetectorIds.FromPublic(finding.DetectorId),
                Action: SanitizerActionIds.RedactNonRestorable,
                OriginalValue: text.Substring(finding.Offset, finding.Length),
                Restorable: false))
            .OrderBy(candidate => candidate.Offset)
            .ThenByDescending(candidate => candidate.Length)
            .ToArray();
    }
}
