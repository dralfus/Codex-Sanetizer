using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

public sealed record LocalRestoreResult(
    RestorationResult Restoration,
    string DisplayText,
    AuditWriteResult AuditWriteResult);

public sealed class LocalRestoreWorkflow
{
    private static readonly Regex RestorablePseudonymPattern = new(
        @"(?<![A-Za-z0-9_])(?:USERNAME_[a-z]+_[a-z]+_[0-9A-F]{4}|(?:SYNTHETIC_BLOCK|SYNTHETIC|CONNECTION|CUSTOMER|PRODUCT|PROJECT|USERNAME|DOMAIN|EMAIL|PATH|CIDR|IP|SYSTEM|URL)_[0-9A-F]{12})(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NonRestorableRedactionPattern = new(
        @"(?<![A-Z0-9_])(?:TOKEN|PRIVATE_KEY|PASSWORD|CONNECTION_STRING|SECRET)_REDACTED(?![A-Z0-9_])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IRestorer _restorer;
    private readonly IAuditSink _auditSink;

    public LocalRestoreWorkflow(IRestorer restorer, IAuditSink auditSink)
    {
        _restorer = restorer ?? throw new ArgumentNullException(nameof(restorer));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    }

    public static LocalRestoreWorkflow CreateProduction(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        layout.EnsureDirectories();
        FileMappingVault.MigrateLegacyDefaultVaultIfNeeded(layout);

        var dataProtector = new WindowsDpapiDataProtector();
        var secretProvider = new DpapiProtectedHmacSecretProvider(
            Path.Combine(layout.RootDirectory, DpapiProtectedHmacSecretProvider.DefaultSecretFileName),
            dataProtector);
        var vault = FileMappingVault.CreateProtected(
            Path.Combine(layout.VaultDirectory, FileMappingVault.DefaultVaultFileName),
            secretProvider.GetOrCreateSecret(),
            dataProtector);

        return new LocalRestoreWorkflow(
            new LocalRestorer(vault),
            new FileAuditSink(layout.AuditDirectory));
    }

    public LocalRestoreResult RestoreText(string sanitizedText)
    {
        ArgumentNullException.ThrowIfNull(sanitizedText);

        var stopwatch = Stopwatch.StartNew();
        return Restore(new RestoreRequest(
            SanitizedText: sanitizedText,
            Replacements: DiscoverReplacements(sanitizedText)));
    }

    public LocalRestoreResult Restore(RestoreRequest restoreRequest)
    {
        ArgumentNullException.ThrowIfNull(restoreRequest);

        var stopwatch = Stopwatch.StartNew();
        var restoration = _restorer.Restore(restoreRequest);
        stopwatch.Stop();

        var auditWriteResult = _auditSink.Write(CreateAuditEvent(restoration, stopwatch.ElapsedMilliseconds));
        if (!auditWriteResult.Succeeded && !string.IsNullOrWhiteSpace(auditWriteResult.WarningCode))
        {
            restoration = restoration with
            {
                Warnings = restoration.Warnings.Concat(new[]
                {
                    new Warning(
                        auditWriteResult.WarningCode,
                        "Restoration audit event could not be written.",
                        WarningSeverity.Warning)
                }).ToArray()
            };
        }

        return new LocalRestoreResult(
            Restoration: restoration,
            DisplayText: LocalRestoreOutputFormatter.Render(restoration),
            AuditWriteResult: auditWriteResult);
    }

    internal static IReadOnlyList<Replacement> DiscoverReplacements(string text)
    {
        var replacements = new List<Replacement>();

        foreach (Match match in RestorablePseudonymPattern.Matches(text).Cast<Match>())
        {
            replacements.Add(new Replacement(
                ContentPartId: "response",
                Offset: match.Index,
                Length: match.Length,
                Type: "pseudonym",
                Placeholder: match.Value,
                Action: PolicyActions.PseudonymizeRestorable,
                Restorable: true));
        }

        foreach (Match match in NonRestorableRedactionPattern.Matches(text).Cast<Match>())
        {
            replacements.Add(new Replacement(
                ContentPartId: "response",
                Offset: match.Index,
                Length: match.Length,
                Type: InferRedactionType(match.Value),
                Placeholder: match.Value,
                Action: PolicyActions.RedactNonRestorable,
                Restorable: false));
        }

        return replacements
            .OrderBy(replacement => replacement.Offset)
            .ThenBy(replacement => replacement.Placeholder, StringComparer.Ordinal)
            .ToArray();
    }

    private static string InferRedactionType(string placeholder)
    {
        return placeholder[..^"_REDACTED".Length].ToLowerInvariant();
    }

    private static AuditEvent CreateAuditEvent(RestorationResult restoration, long elapsedMs)
    {
        var warningCodeCounts = restoration.Warnings
            .GroupBy(warning => warning.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var restoredTotal = restoration.Metadata.RestoredPseudonymCountsByType.Values.Sum();
        var actionCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["restore_restorable"] = restoredTotal,
            ["warning"] = warningCodeCounts.Values.Sum()
        };

        foreach (var item in warningCodeCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            actionCounts[$"warning.{item.Key}"] = item.Value;
        }

        return new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            RequestId: Guid.NewGuid().ToString("N"),
            Application: "local_restore",
            WorkspaceHash: null,
            PolicyProfile: "local_restore",
            Decision: SanitizeDecision.Allow,
            ScannerStatuses: new Dictionary<string, string>
            {
                ["restore"] = restoration.Metadata.LocalSensitive ? "restored_local_sensitive" : "no_local_values_restored"
            },
            EntityCountsByType: restoration.Metadata.RestoredPseudonymCountsByType,
            ActionCounts: actionCounts,
            SpanSummaries: Array.Empty<SpanSummary>(),
            ReplacementSummaries: Array.Empty<ReplacementSummary>(),
            Warnings: warningCodeCounts
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new Warning(
                    item.Key,
                    "Restoration warning code recorded.",
                    WarningSeverity.Warning))
                .ToArray(),
            AdapterMode: "local_restore",
            DurationsMs: new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["restore"] = elapsedMs
            });
    }
}

public static class LocalRestoreOutputFormatter
{
    public static string Render(RestorationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        if (result.Metadata.LocalSensitive)
        {
            builder.AppendLine("LOCAL-SENSITIVE RESTORED OUTPUT");
            builder.AppendLine("Sanitize again before sending this text to a cloud app.");
        }
        else
        {
            builder.AppendLine("NO LOCAL VALUES RESTORED");
        }

        builder.AppendLine();
        builder.AppendLine(result.Text);

        if (result.Metadata.RestoredPseudonymCountsByType.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Restored counts:");
            foreach (var item in result.Metadata.RestoredPseudonymCountsByType.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                builder.AppendLine($"{item.Key}: {item.Value}");
            }
        }

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Warnings:");
            foreach (var warning in result.Warnings.DistinctBy(warning => warning.Code).OrderBy(warning => warning.Code, StringComparer.Ordinal))
            {
                builder.AppendLine($"{warning.Code}: {warning.Message}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
