using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class SpanResolver
{
    public IReadOnlyList<SensitiveCandidate> Resolve(IEnumerable<SensitiveCandidate> candidates)
    {
        var selected = new List<SensitiveCandidate>();

        foreach (var candidate in candidates
            .OrderByDescending(GetRisk)
            .ThenByDescending(candidate => candidate.Length))
        {
            if (selected.Any(selectedCandidate => Overlaps(selectedCandidate, candidate)))
            {
                continue;
            }

            selected.Add(candidate);
        }

        return selected
            .OrderBy(candidate => candidate.Offset)
            .ThenByDescending(candidate => candidate.Length)
            .ToArray();
    }

    private static bool Overlaps(SensitiveCandidate left, SensitiveCandidate right)
    {
        return left.Offset < right.Offset + right.Length
            && right.Offset < left.Offset + left.Length;
    }

    private static int GetRisk(SensitiveCandidate candidate)
    {
        return candidate.Type.Value switch
        {
            SensitiveEntityTypes.ConnectionString => 100,
            SensitiveEntityTypes.PrivateKey => 100,
            SensitiveEntityTypes.Token => 100,
            SensitiveEntityTypes.Password => 100,
            SensitiveEntityTypes.SyntheticBlockMarker => 100,
            SensitiveEntityTypes.SyntheticMarker => 80,
            SensitiveEntityTypes.Url => 60,
            SensitiveEntityTypes.Domain => 60,
            SensitiveEntityTypes.Cidr => 60,
            SensitiveEntityTypes.IpAddress => 60,
            SensitiveEntityTypes.Email => 60,
            SensitiveEntityTypes.FilePath => 60,
            _ => 40
        };
    }
}

internal sealed class ReplacementPlanner
{
    private readonly IMappingVault _mappingVault;

    public ReplacementPlanner(IMappingVault mappingVault)
    {
        _mappingVault = mappingVault;
    }

    public IReadOnlyList<Replacement> Plan(
        ContentText contentText,
        IReadOnlyList<SensitiveCandidate> candidates)
    {
        return candidates
            .Select(candidate => new Replacement(
                ContentPartId: contentText.ResolveContentPartId(candidate.Offset),
                Offset: candidate.Offset,
                Length: candidate.Length,
                Type: candidate.Type.Value,
                Placeholder: CreateReplacementText(candidate),
                Action: candidate.Action.Value,
                Restorable: candidate.Restorable))
            .ToArray();
    }

    private string CreateReplacementText(SensitiveCandidate candidate)
    {
        return candidate.Action == SanitizerActionIds.RedactNonRestorable
            ? GetRedactionPlaceholder(candidate.Type.Value)
            : _mappingVault.GetOrCreatePseudonym(candidate.Type.Value, candidate.OriginalValue);
    }

    private static string GetRedactionPlaceholder(string entityType)
    {
        return entityType switch
        {
            SensitiveEntityTypes.Token => "TOKEN_REDACTED",
            SensitiveEntityTypes.PrivateKey => "PRIVATE_KEY_REDACTED",
            SensitiveEntityTypes.Password => "PASSWORD_REDACTED",
            SensitiveEntityTypes.ConnectionString => "CONNECTION_STRING_REDACTED",
            _ => "SECRET_REDACTED"
        };
    }
}
