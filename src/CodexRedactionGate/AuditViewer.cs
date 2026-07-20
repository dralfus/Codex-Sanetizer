using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record AuditViewerRow(
    DateTimeOffset EventTime,
    string TargetProfile,
    string Decision,
    IReadOnlyDictionary<string, int> ActionCounts,
    IReadOnlyDictionary<string, int> EntityCounts,
    IReadOnlyDictionary<string, string> ScannerStatuses,
    IReadOnlyList<string> WarningCodes,
    IReadOnlyDictionary<string, long> DurationsMs,
    string FailureReasonCode);

public sealed record AuditViewerReport(
    AuditChainVerificationResult Chain,
    IReadOnlyList<AuditViewerRow> Rows);

public sealed record AuditCleanupResult(
    int EventsBefore,
    int EventsDeleted,
    int EventsKept,
    AuditChainVerificationResult Chain);

public static class AuditViewer
{
    public static AuditViewerReport Load(string auditDirectory, int maxEvents = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditDirectory);
        if (maxEvents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "Max events must not be negative.");
        }

        var rows = AuditChainReader.ReadRecords(auditDirectory)
            .OrderByDescending(record => record.Event.Timestamp)
            .Take(maxEvents)
            .Select(record => CreateRow(record.Event))
            .ToArray();

        return new AuditViewerReport(
            Chain: AuditChainVerifier.Verify(auditDirectory),
            Rows: rows);
    }

    public static IReadOnlyList<string> Render(AuditViewerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"chain: {report.Chain.Code}",
            $"events: {report.Chain.EventCount}"
        };

        foreach (var row in report.Rows)
        {
            lines.Add($"event time_utc={row.EventTime:O} target={row.TargetProfile} decision={row.Decision} failure={row.FailureReasonCode}");
            lines.Add($"  actions: {FormatCounts(row.ActionCounts)}");
            lines.Add($"  entities: {FormatCounts(row.EntityCounts)}");
            lines.Add($"  scanner: {FormatStatuses(row.ScannerStatuses)}");
            lines.Add($"  warnings: {FormatList(row.WarningCodes)}");
            lines.Add($"  durations_ms: {FormatDurations(row.DurationsMs)}");
        }

        return lines;
    }

    public static AuditCleanupResult Cleanup(string auditDirectory, int keepEvents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditDirectory);
        if (keepEvents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepEvents), "Keep events must not be negative.");
        }

        var recordsBefore = AuditChainReader.ReadRecords(auditDirectory);
        if (!Directory.Exists(auditDirectory))
        {
            return new AuditCleanupResult(
                EventsBefore: 0,
                EventsDeleted: 0,
                EventsKept: 0,
                Chain: AuditChainVerifier.Verify(auditDirectory));
        }

        var filesToDelete = new DirectoryInfo(auditDirectory)
            .EnumerateFiles("audit-*.json")
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(keepEvents)
            .ToArray();

        foreach (var file in filesToDelete)
        {
            file.Delete();
        }

        AuditChainReader.RebuildChain(auditDirectory);
        AuditChainReader.WriteHead(auditDirectory);
        var chain = AuditChainVerifier.Verify(auditDirectory);

        return new AuditCleanupResult(
            EventsBefore: recordsBefore.Count,
            EventsDeleted: filesToDelete.Length,
            EventsKept: AuditChainReader.ReadRecords(auditDirectory).Count,
            Chain: chain);
    }

    private static AuditViewerRow CreateRow(AuditEvent auditEvent)
    {
        return new AuditViewerRow(
            EventTime: auditEvent.Timestamp,
            TargetProfile: FirstNonEmpty(auditEvent.AdapterMode, auditEvent.Application, auditEvent.PolicyProfile, "unknown"),
            Decision: auditEvent.Decision.ToString(),
            ActionCounts: auditEvent.ActionCounts,
            EntityCounts: auditEvent.EntityCountsByType,
            ScannerStatuses: auditEvent.ScannerStatuses,
            WarningCodes: auditEvent.Warnings
                .Select(warning => warning.Code)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(code => code, StringComparer.Ordinal)
                .ToArray(),
            DurationsMs: auditEvent.DurationsMs,
            FailureReasonCode: GetFailureReasonCode(auditEvent));
    }

    private static string GetFailureReasonCode(AuditEvent auditEvent)
    {
        if (auditEvent.Decision != SanitizeDecision.Block)
        {
            return "none";
        }

        if (auditEvent.ScannerStatuses.TryGetValue("trace.reason", out var traceReason)
            && !string.IsNullOrWhiteSpace(traceReason))
        {
            return traceReason;
        }

        var warningCode = auditEvent.Warnings.FirstOrDefault()?.Code;
        if (!string.IsNullOrWhiteSpace(warningCode))
        {
            return warningCode;
        }

        var scannerFailure = auditEvent.ScannerStatuses
            .FirstOrDefault(item => item.Value is "timeout" or "scanner_error" or "invalid_json" or "configuration_error");

        return string.IsNullOrWhiteSpace(scannerFailure.Value)
            ? "block"
            : scannerFailure.Value;
    }

    private static string FormatCounts(IReadOnlyDictionary<string, int> counts)
    {
        return counts.Count == 0
            ? "none"
            : string.Join(" ", counts.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}"));
    }

    private static string FormatStatuses(IReadOnlyDictionary<string, string> statuses)
    {
        return statuses.Count == 0
            ? "none"
            : string.Join(" ", statuses.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}"));
    }

    private static string FormatDurations(IReadOnlyDictionary<string, long> durations)
    {
        return durations.Count == 0
            ? "none"
            : string.Join(" ", durations.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key}={item.Value}"));
    }

    private static string FormatList(IReadOnlyList<string> items)
    {
        return items.Count == 0 ? "none" : string.Join(",", items);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "unknown";
    }
}
