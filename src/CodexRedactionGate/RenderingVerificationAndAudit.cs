using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

internal sealed class SanitizedTextRenderer
{
    public string Render(string text, IReadOnlyList<Replacement> replacements)
    {
        var rendered = new System.Text.StringBuilder();
        var cursor = 0;

        foreach (var replacement in replacements.OrderBy(replacement => replacement.Offset))
        {
            rendered.Append(text, cursor, replacement.Offset - cursor);
            rendered.Append(replacement.Placeholder);
            cursor = replacement.Offset + replacement.Length;
        }

        rendered.Append(text, cursor, text.Length - cursor);
        return rendered.ToString();
    }
}

internal sealed class SanitizedOutputVerifier
{
    public SanitizedOutputVerificationResult Verify(
        string originalText,
        string sanitizedText,
        IReadOnlyList<Replacement> replacements,
        int expectedReplacementCount)
    {
        if (replacements.Count != expectedReplacementCount)
        {
            return new SanitizedOutputVerificationResult(false, "replacement_count_mismatch");
        }

        foreach (var replacement in replacements)
        {
            if (replacement.Offset < 0
                || replacement.Length < 0
                || replacement.Offset + replacement.Length > originalText.Length)
            {
                return new SanitizedOutputVerificationResult(false, "replacement_span_out_of_range");
            }

            var rawValue = originalText.Substring(replacement.Offset, replacement.Length);
            if (RawValueSurvived(sanitizedText, rawValue, replacement.Type))
            {
                return new SanitizedOutputVerificationResult(false, "raw_span_survived");
            }
        }

        return new SanitizedOutputVerificationResult(true, null);
    }

    private static bool RawValueSurvived(string sanitizedText, string rawValue, string entityType)
    {
        if (rawValue.Length == 0)
        {
            return false;
        }

        return TextSpanUtilities.FindOffsetsForEntity(sanitizedText, rawValue, entityType).Count > 0;
    }
}

internal sealed class AuditEventBuilder
{
    public AuditEvent Create(
        SanitizeRequest request,
        SanitizeDecision decision,
        IReadOnlyList<SanitizedEntity> entities,
        IReadOnlyList<Replacement> replacements,
        IReadOnlyList<Warning> warnings,
        long elapsedMs,
        IReadOnlyDictionary<string, string>? scannerStatuses = null)
    {
        var auditMetadata = GetAuditMetadata(decision);

        return new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            RequestId: Guid.NewGuid().ToString("N"),
            Application: request.Context.Application,
            WorkspaceHash: null,
            PolicyProfile: request.Context.PolicyProfile,
            Decision: decision,
            ScannerStatuses: scannerStatuses ?? new Dictionary<string, string> { ["minimal_sanitizer"] = auditMetadata.ScannerStatus },
            EntityCountsByType: entities
                .GroupBy(entity => entity.Type)
                .ToDictionary(group => group.Key, group => group.Count()),
            ActionCounts: CreateActionCounts(auditMetadata.Action, replacements),
            SpanSummaries: entities
                .Select(entity => new SpanSummary(
                    entity.ContentPartId,
                    entity.Offset,
                    entity.Length,
                    entity.Type,
                    entity.DetectorId))
                .ToArray(),
            ReplacementSummaries: replacements
                .Select(replacement => new ReplacementSummary(
                    replacement.Placeholder,
                    replacement.Type,
                    replacement.Action))
                .ToArray(),
            Warnings: warnings,
            AdapterMode: null,
            DurationsMs: new Dictionary<string, long> { ["total"] = elapsedMs });
    }

    private static (string Action, string ScannerStatus) GetAuditMetadata(SanitizeDecision decision)
    {
        return decision switch
        {
            SanitizeDecision.Allow => ("allow", "allow_path"),
            SanitizeDecision.Block => (SanitizerPipelineConstants.BlockAction, "synthetic_block_marker_found"),
            _ => (SanitizerPipelineConstants.SyntheticAction, "confirm_path")
        };
    }

    private static IReadOnlyDictionary<string, int> CreateActionCounts(
        string fallbackAction,
        IReadOnlyList<Replacement> replacements)
    {
        if (replacements.Count == 0)
        {
            return new Dictionary<string, int> { [fallbackAction] = 1 };
        }

        return replacements
            .GroupBy(replacement => replacement.Action)
            .ToDictionary(group => group.Key, group => group.Count());
    }
}

internal sealed class SanitizationResultAssembler
{
    private readonly AuditEventBuilder _auditEventBuilder = new();
    private readonly IAuditSink? _auditSink;

    public SanitizationResultAssembler(IAuditSink? auditSink = null)
    {
        _auditSink = auditSink;
    }

    public SanitizationResult Allow(
        SanitizeRequest request,
        string sanitizedText,
        long elapsedMs,
        IReadOnlyDictionary<string, string>? scannerStatuses = null)
    {
        var result = new SanitizationResult(
            Decision: SanitizeDecision.Allow,
            SanitizedText: sanitizedText,
            Entities: Array.Empty<SanitizedEntity>(),
            Replacements: Array.Empty<Replacement>(),
            Warnings: Array.Empty<Warning>(),
            AuditEvent: _auditEventBuilder.Create(
                request,
                SanitizeDecision.Allow,
                Array.Empty<SanitizedEntity>(),
                Array.Empty<Replacement>(),
                Array.Empty<Warning>(),
                elapsedMs,
                scannerStatuses),
            RestoreHandle: null);

        return PersistAuditIfConfigured(request, result, elapsedMs);
    }

    public SanitizationResult Confirm(
        SanitizeRequest request,
        string sanitizedText,
        IReadOnlyList<SanitizedEntity> entities,
        IReadOnlyList<Replacement> replacements,
        long elapsedMs,
        IReadOnlyDictionary<string, string>? scannerStatuses = null)
    {
        var result = new SanitizationResult(
            Decision: SanitizeDecision.Confirm,
            SanitizedText: sanitizedText,
            Entities: entities,
            Replacements: replacements,
            Warnings: Array.Empty<Warning>(),
            AuditEvent: _auditEventBuilder.Create(
                request,
                SanitizeDecision.Confirm,
                entities,
                replacements,
                Array.Empty<Warning>(),
                elapsedMs,
                scannerStatuses),
            RestoreHandle: null);

        return PersistAuditIfConfigured(request, result, elapsedMs);
    }

    public SanitizationResult Block(
        SanitizeRequest request,
        IReadOnlyList<SanitizedEntity> entities,
        IReadOnlyList<Replacement> replacements,
        IReadOnlyList<Warning> warnings,
        long elapsedMs,
        IReadOnlyDictionary<string, string>? scannerStatuses = null)
    {
        var result = new SanitizationResult(
            Decision: SanitizeDecision.Block,
            SanitizedText: string.Empty,
            Entities: entities,
            Replacements: replacements,
            Warnings: warnings,
            AuditEvent: _auditEventBuilder.Create(
                request,
                SanitizeDecision.Block,
                entities,
                replacements,
                warnings,
                elapsedMs,
                scannerStatuses),
            RestoreHandle: null);

        return PersistAuditIfConfigured(request, result, elapsedMs);
    }

    public static IReadOnlyList<SanitizedEntity> CreateEntities(
        ContentText contentText,
        IReadOnlyList<SensitiveCandidate> candidates)
    {
        return candidates
            .Select(candidate => new SanitizedEntity(
                ContentPartId: contentText.ResolveContentPartId(candidate.Offset),
                Offset: candidate.Offset,
                Length: candidate.Length,
                Type: candidate.Type.Value,
                DetectorId: candidate.DetectorId.Value,
                Action: candidate.Action.Value))
            .ToArray();
    }

    private SanitizationResult PersistAuditIfConfigured(
        SanitizeRequest request,
        SanitizationResult result,
        long elapsedMs)
    {
        if (_auditSink is null)
        {
            return result;
        }

        var writeResult = _auditSink.Write(result.AuditEvent);
        if (writeResult.Succeeded)
        {
            return result;
        }

        var warning = new Warning(
            Code: writeResult.WarningCode ?? "audit_write_failed",
            Message: "Local audit persistence failed.",
            Severity: result.Decision == SanitizeDecision.Allow ? WarningSeverity.Warning : WarningSeverity.Error);
        var warnings = result.Warnings.Concat(new[] { warning }).ToArray();

        if (result.Decision == SanitizeDecision.Confirm)
        {
            return new SanitizationResult(
                Decision: SanitizeDecision.Block,
                SanitizedText: string.Empty,
                Entities: result.Entities,
                Replacements: Array.Empty<Replacement>(),
                Warnings: warnings,
                AuditEvent: _auditEventBuilder.Create(
                    request,
                    SanitizeDecision.Block,
                    result.Entities,
                    Array.Empty<Replacement>(),
                    warnings,
                    elapsedMs,
                    result.AuditEvent.ScannerStatuses),
                RestoreHandle: null);
        }

        return result with
        {
            Warnings = warnings,
            AuditEvent = _auditEventBuilder.Create(
                request,
                result.Decision,
                result.Entities,
                result.Replacements,
                warnings,
                elapsedMs,
                result.AuditEvent.ScannerStatuses)
        };
    }
}
