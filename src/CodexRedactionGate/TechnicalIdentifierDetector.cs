using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class TechnicalIdentifierDetector : ISensitiveDetector
{
    private readonly PublicAllowlistEvaluator _allowlistEvaluator;

    public TechnicalIdentifierDetector(PublicAllowlistEvaluator allowlistEvaluator)
    {
        _allowlistEvaluator = allowlistEvaluator;
    }

    public IReadOnlyList<SensitiveCandidate> Detect(
        string text,
        string contentPartId,
        SecretScanResult? scannerResult)
    {
        var candidates = new List<SensitiveCandidate>();

        AddUrlCandidates(text, contentPartId, candidates);
        AddDomainCandidates(text, contentPartId, candidates);
        AddConnectionStringCandidates(text, contentPartId, candidates);
        AddEmailCandidates(text, contentPartId, candidates);
        AddWindowsPromptUserCandidates(text, contentPartId, candidates);
        AddPathCandidates(text, contentPartId, candidates);
        AddCidrCandidates(text, contentPartId, candidates);
        AddIpCandidates(text, contentPartId, candidates);

        return candidates
            .OrderBy(candidate => candidate.Offset)
            .ThenByDescending(candidate => candidate.Length)
            .ToArray();
    }

    private void AddUrlCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.Url.Matches(text))
        {
            var value = TextSpanUtilities.TrimTrailingUrlPunctuation(match.Value);

            if (_allowlistEvaluator.IsPublicAllowlistedUrl(value) || !_allowlistEvaluator.IsInternalUrl(value))
            {
                continue;
            }

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Url),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: value,
                Restorable: true));
        }
    }

    private void AddDomainCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.Domain.Matches(text))
        {
            var value = match.Value.TrimEnd('.');

            if (_allowlistEvaluator.IsInsidePublicAllowlistedUrl(text, match.Index)
                || !_allowlistEvaluator.IsInternalDomain(value))
            {
                continue;
            }

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Domain),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: value,
                Restorable: true));
        }
    }

    private static void AddConnectionStringCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.ConnectionString.Matches(text))
        {
            var value = match.Value.TrimEnd(';');

            if (!TextSpanUtilities.ContainsPasswordKey(value))
            {
                continue;
            }

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.ConnectionString),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.RedactNonRestorable,
                OriginalValue: value,
                Restorable: false));
        }
    }

    private static void AddEmailCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.Email.Matches(text))
        {
            var value = TextSpanUtilities.TrimTrailingUrlPunctuation(match.Value);

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Email),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: value,
                Restorable: true));
        }
    }

    private static void AddWindowsPromptUserCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.WindowsPromptUserPath.Matches(text))
        {
            var username = match.Groups["username"];

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: username.Index,
                Length: username.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Username),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: username.Value,
                Restorable: true));
        }
    }

    private static void AddPathCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.WindowsUserPath.Matches(text))
        {
            var value = TextSpanUtilities.TrimTrailingPathPunctuation(match.Value);

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.FilePath),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: value,
                Restorable: true));
        }

        foreach (Match match in SanitizerRegexes.UnixPath.Matches(text))
        {
            var value = TextSpanUtilities.TrimTrailingPathPunctuation(match.Value);

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.FilePath),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: value,
                Restorable: true));
        }
    }

    private static void AddCidrCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.Cidr.Matches(text))
        {
            var value = match.Value;

            if (!TextSpanUtilities.IsPrivateCidr(value))
            {
                continue;
            }

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.Cidr),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: value,
                Restorable: true));
        }
    }

    private static void AddIpCandidates(
        string text,
        string contentPartId,
        List<SensitiveCandidate> candidates)
    {
        foreach (Match match in SanitizerRegexes.Ipv4.Matches(text))
        {
            var value = match.Value;

            if (!TextSpanUtilities.IsPrivateIpv4(value))
            {
                continue;
            }

            candidates.Add(new SensitiveCandidate(
                ContentPartId: contentPartId,
                Offset: match.Index,
                Length: value.Length,
                Type: SensitiveEntityTypeIds.FromPublic(SensitiveEntityTypes.IpAddress),
                DetectorId: SensitiveDetectorIds.Technical,
                Action: SanitizerActionIds.PseudonymizeRestorable,
                OriginalValue: value,
                Restorable: true));
        }
    }
}
