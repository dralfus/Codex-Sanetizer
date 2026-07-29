using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

internal sealed class RecoveryRequiredSanitizer : ISanitizer
{
    public SanitizationResult Sanitize(SanitizeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var warning = new Warning(
            LocalProtectionRecovery.RecoveryRequiredCode,
            "Local protection recovery is required before cloud submission.",
            WarningSeverity.Error);
        return new SanitizationResult(
            Decision: SanitizeDecision.Block,
            SanitizedText: string.Empty,
            Entities: Array.Empty<SanitizedEntity>(),
            Replacements: Array.Empty<Replacement>(),
            Warnings: new[] { warning },
            AuditEvent: new AuditEvent(
                Timestamp: DateTimeOffset.UtcNow,
                RequestId: Guid.NewGuid().ToString("N"),
                Application: request.Context.Application,
                WorkspaceHash: null,
                PolicyProfile: request.Context.PolicyProfile,
                Decision: SanitizeDecision.Block,
                ScannerStatuses: new Dictionary<string, string>
                {
                    ["local_protection"] = LocalProtectionRecovery.RecoveryRequiredCode
                },
                EntityCountsByType: new Dictionary<string, int>(),
                ActionCounts: new Dictionary<string, int>(),
                SpanSummaries: Array.Empty<SpanSummary>(),
                ReplacementSummaries: Array.Empty<ReplacementSummary>(),
                Warnings: new[] { warning },
                AdapterMode: "recovery_required",
                DurationsMs: new Dictionary<string, long>()),
            RestoreHandle: null);
    }
}
