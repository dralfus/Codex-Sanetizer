using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexRedactionGate;

public static class AuditInspection
{
    public static bool Contains(AuditEvent auditEvent, string value)
    {
        return EnumerateStrings(auditEvent)
            .Any(item => item.Contains(value, StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateStrings(AuditEvent auditEvent)
    {
        var topLevelStrings = new[]
        {
            auditEvent.RequestId,
            auditEvent.Application,
            auditEvent.WorkspaceHash,
            auditEvent.PolicyProfile,
            auditEvent.AdapterMode
        };

        return topLevelStrings
            .Where(item => item is not null)
            .Select(item => item!)
            .Concat(auditEvent.ScannerStatuses.Keys)
            .Concat(auditEvent.ScannerStatuses.Values)
            .Concat(auditEvent.EntityCountsByType.Keys)
            .Concat(auditEvent.ActionCounts.Keys)
            .Concat(auditEvent.SpanSummaries.SelectMany(span => new[] { span.ContentPartId, span.Type, span.DetectorId }))
            .Concat(auditEvent.ReplacementSummaries.SelectMany(replacement => new[] { replacement.Pseudonym, replacement.Type, replacement.Action }))
            .Concat(auditEvent.Warnings.SelectMany(warning => new[] { warning.Code, warning.Message }));
    }
}
