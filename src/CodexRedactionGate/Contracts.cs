using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public enum SanitizeDecision
{
    Allow,
    Confirm,
    Block
}

public static class ContentSources
{
    public const string PromptText = "prompt_text";
    public const string Clipboard = "clipboard";
    public const string TextAttachment = "text_attachment";
    public const string FileSnippet = "file_snippet";
    public const string ToolOutput = "tool_output";
}

public sealed record SanitizeRequest(
    IReadOnlyList<ContentPart> ContentParts,
    SanitizationContext Context,
    SanitizationOptions Options);

public sealed record ContentPart(
    string Id,
    string ContentSource,
    string RawText,
    IReadOnlyDictionary<string, string> SourceMetadata);

public sealed record SanitizationContext(
    string? Application,
    string? WorkspacePath,
    string? ProjectId,
    string? SessionId,
    string? PolicyProfile);

public sealed record SanitizationOptions(
    bool AllowSessionAliases,
    bool AllowSecretStorage,
    string ConfirmationMode);

public sealed record SanitizationResult(
    SanitizeDecision Decision,
    string SanitizedText,
    IReadOnlyList<SanitizedEntity> Entities,
    IReadOnlyList<Replacement> Replacements,
    IReadOnlyList<Warning> Warnings,
    AuditEvent AuditEvent,
    string? RestoreHandle);

public sealed record SanitizedEntity(
    string ContentPartId,
    int Offset,
    int Length,
    string Type,
    string DetectorId,
    string Action);

public sealed record Replacement(
    string ContentPartId,
    int Offset,
    int Length,
    string Type,
    string Placeholder,
    string Action,
    bool Restorable);

public sealed record Warning(
    string Code,
    string Message,
    WarningSeverity Severity);

public enum WarningSeverity
{
    Info,
    Warning,
    Error
}

public sealed record AuditEvent(
    DateTimeOffset Timestamp,
    string RequestId,
    string? Application,
    string? WorkspaceHash,
    string? PolicyProfile,
    SanitizeDecision Decision,
    IReadOnlyDictionary<string, string> ScannerStatuses,
    IReadOnlyDictionary<string, int> EntityCountsByType,
    IReadOnlyDictionary<string, int> ActionCounts,
    IReadOnlyList<SpanSummary> SpanSummaries,
    IReadOnlyList<ReplacementSummary> ReplacementSummaries,
    IReadOnlyList<Warning> Warnings,
    string? AdapterMode,
    IReadOnlyDictionary<string, long> DurationsMs);

public sealed record SpanSummary(
    string ContentPartId,
    int Offset,
    int Length,
    string Type,
    string DetectorId);

public sealed record ReplacementSummary(
    string Pseudonym,
    string Type,
    string Action);
