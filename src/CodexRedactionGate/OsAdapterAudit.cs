using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public sealed record OsAdapterAuditEvent(
    DateTimeOffset Timestamp,
    string EventId,
    string Status,
    string? SurfaceProfileId,
    string? Decision,
    bool Applied,
    bool Submitted,
    IReadOnlyDictionary<string, string> Diagnostics);

public interface IOsAdapterAuditSink
{
    void Write(OsAdapterAuditEvent auditEvent);
}

public sealed class InMemoryOsAdapterAuditSink : IOsAdapterAuditSink
{
    public List<OsAdapterAuditEvent> Events { get; } = new();

    public void Write(OsAdapterAuditEvent auditEvent)
    {
        Events.Add(auditEvent);
    }
}

public static class OsAdapterAudit
{
    public static OsAdapterAuditEvent FromResult(OsInteractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new OsAdapterAuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            EventId: Guid.NewGuid().ToString("N"),
            Status: result.Status,
            SurfaceProfileId: result.Surface?.ProfileId,
            Decision: result.SanitizationResult?.Decision.ToString(),
            Applied: result.Applied,
            Submitted: result.Submitted,
            Diagnostics: result.Diagnostics);
    }
}
