using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodexRedactionGate;

public static class ProjectFileProtectionStatusValues
{
    public const string NotConfigured = "not_configured";
    public const string Protected = "protected";
    public const string BrokerDemoOnly = "broker_demo_only";
    public const string UnprotectedNoBroker = "unprotected_no_broker";

    public static string ToSafeDisplayValue(string value)
    {
        return value is Protected or BrokerDemoOnly or NotConfigured or UnprotectedNoBroker
            ? value
            : "unsupported";
    }
}

public static class ProjectFileIngressStatusValues
{
    public const string Unsupported = "unsupported";
    public const string NotConfigured = "not_configured";
}

public sealed record ProjectFileIngressStatus(
    bool PreCloudBoundaryAvailable,
    bool MustBlockUnroutedContext,
    string Status,
    string Code,
    string WorkspaceId);

public static class ProjectFileIngressStatusInspector
{
    public static ProjectFileIngressStatus Inspect(DefaultStorageLayout layout, string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        return FromWorkspaceStatus(ProtectedWorkspaceStore.GetStatus(layout, workspacePath));
    }

    internal static ProjectFileIngressStatus FromWorkspaceStatus(ProtectedWorkspaceStatus workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        if (string.Equals(workspace.Code, "protected_workspace_status_unavailable", StringComparison.Ordinal))
        {
            return new ProjectFileIngressStatus(
                PreCloudBoundaryAvailable: false,
                MustBlockUnroutedContext: true,
                Status: ProjectFileIngressStatusValues.Unsupported,
                Code: "project_file_ingress_unsupported",
                WorkspaceId: workspace.WorkspaceId);
        }

        return workspace.Protected
            ? new ProjectFileIngressStatus(
                PreCloudBoundaryAvailable: false,
                MustBlockUnroutedContext: true,
                Status: ProjectFileIngressStatusValues.Unsupported,
                Code: "project_file_ingress_unsupported",
                WorkspaceId: workspace.WorkspaceId)
            : new ProjectFileIngressStatus(
                PreCloudBoundaryAvailable: false,
                MustBlockUnroutedContext: false,
                Status: ProjectFileIngressStatusValues.NotConfigured,
                Code: "protected_workspace_not_registered",
                WorkspaceId: workspace.WorkspaceId);
    }
}

public sealed record ProtectedWorkspaceRegistrationResult(
    bool Succeeded,
    string Code,
    string WorkspaceId,
    string StorePath);

public sealed record ProtectedWorkspaceStatus(
    bool Protected,
    string Code,
    string WorkspaceId,
    string StorePath);

public sealed record ProtectedWorkspaceRecord(
    string WorkspaceId,
    bool Enabled,
    DateTimeOffset UpdatedAt);

public sealed record ProtectedWorkspaceDocument(
    int Version,
    IReadOnlyList<ProtectedWorkspaceRecord> Workspaces);

public static class ProtectedWorkspaceStore
{
    public static string DefaultPath(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.SettingsDirectory, "protected-workspaces.json");
    }

    public static ProtectedWorkspaceRegistrationResult Protect(DefaultStorageLayout layout, string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        Directory.CreateDirectory(layout.SettingsDirectory);
        var path = DefaultPath(layout);
        var workspaceId = RawFreeIdentity.HashPath(workspacePath);
        var document = LoadDocument(path);
        var records = document.Workspaces
            .Where(record => !string.Equals(record.WorkspaceId, workspaceId, StringComparison.Ordinal))
            .Append(new ProtectedWorkspaceRecord(workspaceId, Enabled: true, UpdatedAt: DateTimeOffset.UtcNow))
            .OrderBy(record => record.WorkspaceId, StringComparer.Ordinal)
            .ToArray();
        SaveDocument(path, new ProtectedWorkspaceDocument(1, records));
        return new ProtectedWorkspaceRegistrationResult(true, "protected_workspace_registered", workspaceId, path);
    }

    public static ProtectedWorkspaceStatus GetStatus(DefaultStorageLayout layout, string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var path = DefaultPath(layout);
        var workspaceId = RawFreeIdentity.HashPath(workspacePath);
        if (!TryLoadDocument(path, out var document))
        {
            return new ProtectedWorkspaceStatus(
                Protected: false,
                Code: "protected_workspace_status_unavailable",
                WorkspaceId: workspaceId,
                StorePath: path);
        }

        var active = document.Workspaces.Any(record =>
            string.Equals(record.WorkspaceId, workspaceId, StringComparison.Ordinal)
            && record.Enabled);
        return new ProtectedWorkspaceStatus(
            active,
            active ? "protected_workspace_registered" : "protected_workspace_not_registered",
            workspaceId,
            path);
    }

    internal static bool HasProtectedWorkspace(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        return TryLoadDocument(DefaultPath(layout), out var document)
            && document.Workspaces.Any(record => record.Enabled);
    }

    private static ProtectedWorkspaceDocument LoadDocument(string path)
    {
        return TryLoadDocument(path, out var document)
            ? document
            : new ProtectedWorkspaceDocument(1, Array.Empty<ProtectedWorkspaceRecord>());
    }

    private static bool TryLoadDocument(string path, out ProtectedWorkspaceDocument document)
    {
        document = new ProtectedWorkspaceDocument(1, Array.Empty<ProtectedWorkspaceRecord>());
        if (!File.Exists(path))
        {
            return true;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ProtectedWorkspaceDocument>(File.ReadAllText(path));
            if (loaded is null)
            {
                return false;
            }

            document = loaded;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or JsonException)
        {
            return false;
        }
    }

    private static void SaveDocument(string path, ProtectedWorkspaceDocument document)
    {
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        AtomicFileWriter.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
    }
}

internal static class ProjectFileProtectionStatusInspector
{
    public static string Inspect(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        try
        {
            return ProtectedWorkspaceStore.HasProtectedWorkspace(layout)
                ? ProjectFileProtectionStatusValues.BrokerDemoOnly
                : ProjectFileProtectionStatusValues.NotConfigured;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ProjectFileProtectionStatusValues.UnprotectedNoBroker;
        }
    }
}

public sealed record ProjectFileBrokerOptions(
    long MaxBytes,
    bool RequireProtectedWorkspace)
{
    public static ProjectFileBrokerOptions DemoDefault { get; } = new(
        MaxBytes: PlainTextAttachmentOptions.Default.MaxBytes,
        RequireProtectedWorkspace: false);

    public static ProjectFileBrokerOptions ProtectedWorkspaceDefault { get; } = new(
        MaxBytes: PlainTextAttachmentOptions.Default.MaxBytes,
        RequireProtectedWorkspace: true);
}

public sealed record SanitizedVirtualFile(
    string SourceId,
    string? WorkspaceId,
    string VirtualPath,
    string ContentHash,
    string Extension,
    long OriginalLength,
    SanitizeDecision Decision,
    string SanitizedText,
    int ReplacementCount,
    IReadOnlyDictionary<string, int> EntityCountsByType,
    IReadOnlyList<Replacement> Replacements);

public sealed record ProjectFileBrokerResult(
    bool Succeeded,
    string Code,
    SanitizedVirtualFile? VirtualFile,
    IReadOnlyList<Warning> Warnings,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record SanitizedToolOutput(
    string ToolOutputIdHash,
    string? WorkspaceId,
    string OutputHash,
    SanitizeDecision Decision,
    string SanitizedText,
    int ReplacementCount,
    IReadOnlyDictionary<string, int> EntityCountsByType);

public sealed record ProjectToolOutputResult(
    bool Succeeded,
    string Code,
    SanitizedToolOutput? ToolOutput,
    IReadOnlyList<Warning> Warnings,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record ProjectFilePatchDryRunRequest(
    SanitizedVirtualFile SourceFile,
    string WorkspacePath,
    string TargetFilePath,
    string SanitizedEdit);

public sealed record ProjectFilePatchDryRunResult(
    bool Succeeded,
    string Code,
    string? PreviewText,
    bool LocalSensitive,
    IReadOnlyList<Warning> Warnings,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record ProjectFilePatchApplyRequest(
    ProjectFilePatchDryRunRequest DryRunRequest,
    bool Approved);

public sealed record ProjectFilePatchApplyResult(
    bool Succeeded,
    string Code,
    bool Written,
    bool LocalSensitive,
    AuditWriteResult AuditWriteResult,
    IReadOnlyList<Warning> Warnings,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record ProjectFileBypassPolicy(bool RequireBrokerOnlyFileContext)
{
    public static ProjectFileBypassPolicy BrokerOnlyDefault { get; } = new(RequireBrokerOnlyFileContext: true);
}

public sealed record ProjectFileBypassResult(
    bool Allowed,
    string Code,
    IReadOnlyList<Warning> Warnings,
    IReadOnlyDictionary<string, string> Diagnostics);

public sealed record ProjectFileProductSmokeReport(
    bool Passed,
    bool ReadSanitizedVirtualFilePassed,
    bool ToolOutputSanitizedPassed,
    bool ApprovedWritePassed,
    bool UnsupportedFileBlockedPassed,
    bool BypassBlockedPassed,
    bool BrokerWorkflowPassed,
    bool RawFreeAuditEvidencePassed,
    int AuditEventCount,
    int ReplacementCount);

public sealed class ProjectFileContextBroker
{
    private readonly ISanitizer _sanitizer;
    private readonly DefaultStorageLayout _layout;
    private readonly ProjectFileBrokerOptions _options;

    public ProjectFileContextBroker(
        ISanitizer sanitizer,
        DefaultStorageLayout layout,
        ProjectFileBrokerOptions? options = null)
    {
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _options = options ?? ProjectFileBrokerOptions.DemoDefault;
    }

    public ProjectFileBrokerResult CreateSanitizedVirtualFile(string filePath, string? workspacePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var normalizedFilePath = Path.GetFullPath(filePath);
        var workspaceId = string.IsNullOrWhiteSpace(workspacePath)
            ? null
            : RawFreeIdentity.HashPath(workspacePath!);
        var sourceId = RawFreeIdentity.HashPath(normalizedFilePath);
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source_id"] = sourceId,
            ["workspace_id"] = workspaceId ?? "none",
            ["raw_file_content_recorded"] = "false",
            ["raw_file_path_recorded"] = "false",
            ["broker_routed"] = "true",
            ["broker_only_file_context_required"] = _options.RequireProtectedWorkspace.ToString().ToLowerInvariant(),
            ["broker_mode"] = _options.RequireProtectedWorkspace ? "protected_workspace" : "demo"
        };

        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            var containment = ProjectFileSelectionGuard.CheckWorkspaceContainment(normalizedFilePath, workspacePath!);
            diagnostics["workspace_containment"] = containment.Code;
            if (!containment.Succeeded)
            {
                return Failure(containment.Code, sourceId, workspaceId, diagnostics);
            }

            if (_options.RequireProtectedWorkspace)
            {
                var protectedWorkspace = ProtectedWorkspaceStore.GetStatus(_layout, workspacePath!);
                diagnostics["protected_workspace"] = protectedWorkspace.Protected.ToString().ToLowerInvariant();
                if (!protectedWorkspace.Protected)
                {
                    return Failure(protectedWorkspace.Code, sourceId, workspaceId, diagnostics);
                }
            }
        }
        else if (_options.RequireProtectedWorkspace)
        {
            return Failure("protected_workspace_required", sourceId, workspaceId, diagnostics);
        }

        var intake = PlainTextAttachmentIntake.ReadFile(
            normalizedFilePath,
            sourceId,
            new PlainTextAttachmentOptions(_options.MaxBytes));
        diagnostics["file_intake"] = intake.Code;
        if (!intake.Succeeded)
        {
            return Failure(intake.Code, sourceId, workspaceId, diagnostics, intake.Warnings);
        }

        var pathResult = _sanitizer.Sanitize(CreatePathRequest(normalizedFilePath, workspacePath));
        var contentResult = _sanitizer.Sanitize(CreateFileRequest(intake.ContentPart, workspacePath));
        var contentBytes = Encoding.UTF8.GetBytes(intake.ContentPart.RawText);
        var extension = Path.GetExtension(normalizedFilePath);
        var contentHash = RawFreeIdentity.HashBytes(contentBytes);
        var virtualPath = BuildVirtualPath(sourceId, extension, pathResult);
        var virtualFile = new SanitizedVirtualFile(
            SourceId: sourceId,
            WorkspaceId: workspaceId,
            VirtualPath: virtualPath,
            ContentHash: contentHash,
            Extension: string.IsNullOrWhiteSpace(extension) ? "none" : extension.ToLowerInvariant(),
            OriginalLength: contentBytes.LongLength,
            Decision: contentResult.Decision,
            SanitizedText: contentResult.SanitizedText,
            ReplacementCount: contentResult.Replacements.Count,
            EntityCountsByType: contentResult.AuditEvent.EntityCountsByType,
            Replacements: contentResult.Replacements);
        diagnostics["content_hash"] = contentHash;
        diagnostics["path_decision"] = CliOutputFormatting.FormatDecision(pathResult.Decision);
        diagnostics["content_decision"] = CliOutputFormatting.FormatDecision(contentResult.Decision);
        diagnostics["replacement_count"] = contentResult.Replacements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var item in contentResult.AuditEvent.EntityCountsByType.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            diagnostics[$"entity_count.{item.Key}"] = item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ProjectFileBrokerResult(
            Succeeded: contentResult.Decision is SanitizeDecision.Allow or SanitizeDecision.Confirm,
            Code: contentResult.Decision == SanitizeDecision.Block ? "sanitized_virtual_file_blocked" : "sanitized_virtual_file_ready",
            VirtualFile: virtualFile,
            Warnings: contentResult.Warnings,
            Diagnostics: diagnostics);
    }

    public ProjectToolOutputResult SanitizeManagedToolOutput(
        string toolOutput,
        string workspacePath,
        string toolOutputId = "tool-output")
    {
        ArgumentNullException.ThrowIfNull(toolOutput);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolOutputId);

        var workspaceId = RawFreeIdentity.HashPath(workspacePath);
        var outputHash = RawFreeIdentity.HashBytes(Encoding.UTF8.GetBytes(toolOutput));
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tool_output_id_hash"] = RawFreeIdentity.HashString(toolOutputId),
            ["workspace_id"] = workspaceId,
            ["output_hash"] = outputHash,
            ["raw_tool_output_recorded"] = "false",
            ["tool_output_managed"] = "true",
            ["broker_routed"] = "true",
            ["broker_only_file_context_required"] = _options.RequireProtectedWorkspace.ToString().ToLowerInvariant()
        };

        if (_options.RequireProtectedWorkspace)
        {
            var protectedWorkspace = ProtectedWorkspaceStore.GetStatus(_layout, workspacePath);
            diagnostics["protected_workspace"] = protectedWorkspace.Protected.ToString().ToLowerInvariant();
            if (!protectedWorkspace.Protected)
            {
                return ToolOutputFailure(protectedWorkspace.Code, diagnostics);
            }
        }

        var result = _sanitizer.Sanitize(new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart(toolOutputId, ContentSources.ToolOutput, toolOutput, new Dictionary<string, string>
                {
                    ["source_kind"] = "project_file_tool_output",
                    ["is_broker_managed"] = "true"
                })
            },
            Context: new SanitizationContext("project-file-broker", workspacePath, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none")));
        diagnostics["content_decision"] = CliOutputFormatting.FormatDecision(result.Decision);
        diagnostics["replacement_count"] = result.Replacements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var item in result.AuditEvent.EntityCountsByType.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            diagnostics[$"entity_count.{item.Key}"] = item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ProjectToolOutputResult(
            Succeeded: result.Decision is SanitizeDecision.Allow or SanitizeDecision.Confirm,
            Code: result.Decision == SanitizeDecision.Block ? "managed_tool_output_blocked" : "managed_tool_output_sanitized",
            ToolOutput: new SanitizedToolOutput(
                ToolOutputIdHash: RawFreeIdentity.HashString(toolOutputId),
                WorkspaceId: workspaceId,
                OutputHash: outputHash,
                Decision: result.Decision,
                SanitizedText: result.SanitizedText,
                ReplacementCount: result.Replacements.Count,
                EntityCountsByType: result.AuditEvent.EntityCountsByType),
            Warnings: result.Warnings,
            Diagnostics: diagnostics);
    }

    public static ProjectToolOutputResult ReportUnmanagedToolOutput(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        return ToolOutputFailure(
            "unmanaged_tool_output_unprotected",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["workspace_id"] = RawFreeIdentity.HashPath(workspacePath),
                ["tool_output_managed"] = "false",
                ["raw_tool_output_recorded"] = "false"
            });
    }

    private static ProjectFileBrokerResult Failure(
        string code,
        string sourceId,
        string? workspaceId,
        Dictionary<string, string> diagnostics,
        IReadOnlyList<Warning>? warnings = null)
    {
        diagnostics["source_id"] = sourceId;
        diagnostics["workspace_id"] = workspaceId ?? "none";
        return new ProjectFileBrokerResult(
            Succeeded: false,
            Code: code,
            VirtualFile: null,
            Warnings: warnings ?? new[]
            {
                new Warning(code, "Project file could not be represented as a sanitized virtual file.", WarningSeverity.Error)
            },
            Diagnostics: diagnostics);
    }

    private static ProjectToolOutputResult ToolOutputFailure(
        string code,
        IReadOnlyDictionary<string, string> diagnostics)
    {
        return new ProjectToolOutputResult(
            Succeeded: false,
            Code: code,
            ToolOutput: null,
            Warnings: new[]
            {
                new Warning(code, "Tool output is not protected by the project file broker.", WarningSeverity.Warning)
            },
            Diagnostics: diagnostics);
    }

    private static SanitizeRequest CreatePathRequest(string filePath, string? workspacePath)
    {
        return new SanitizeRequest(
            ContentParts: new[]
            {
                new ContentPart("path", ContentSources.FileSnippet, filePath, new Dictionary<string, string>
                {
                    ["source_kind"] = "project_file_path"
                })
            },
            Context: new SanitizationContext("project-file-broker", workspacePath, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none"));
    }

    private static SanitizeRequest CreateFileRequest(ContentPart contentPart, string? workspacePath)
    {
        return new SanitizeRequest(
            ContentParts: new[] { contentPart },
            Context: new SanitizationContext("project-file-broker", workspacePath, null, null, "default"),
            Options: new SanitizationOptions(false, false, "none"));
    }

    private static string BuildVirtualPath(string sourceId, string extension, SanitizationResult pathResult)
    {
        var suffix = string.IsNullOrWhiteSpace(extension) ? ".txt" : extension.ToLowerInvariant();
        if (pathResult.Replacements.Count > 0 && !pathResult.SanitizedText.Contains(Path.DirectorySeparatorChar))
        {
            return pathResult.SanitizedText;
        }

        return $"{sourceId}{suffix}";
    }
}

public sealed class ProjectFilePatchDryRun
{
    private readonly Func<RestoreRequest, RestorationResult> _restore;
    private readonly DefaultStorageLayout _layout;

    public ProjectFilePatchDryRun(Func<RestoreRequest, RestorationResult> restore, DefaultStorageLayout layout)
    {
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    public ProjectFilePatchDryRunResult Preview(ProjectFilePatchDryRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var expectedWorkspaceId = RawFreeIdentity.HashPath(request.WorkspacePath);
        var expectedSourceId = RawFreeIdentity.HashPath(request.TargetFilePath);
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace_id"] = expectedWorkspaceId,
            ["source_id"] = expectedSourceId,
            ["expected_content_hash"] = request.SourceFile.ContentHash,
            ["write_performed"] = "false",
            ["raw_patch_recorded"] = "false"
        };

        if (!string.Equals(request.SourceFile.WorkspaceId, expectedWorkspaceId, StringComparison.Ordinal))
        {
            return Failure("workspace_mismatch", diagnostics);
        }

        if (!string.Equals(request.SourceFile.SourceId, expectedSourceId, StringComparison.Ordinal))
        {
            return Failure("source_file_mismatch", diagnostics);
        }

        var protectedWorkspace = ProtectedWorkspaceStore.GetStatus(_layout, request.WorkspacePath);
        diagnostics["protected_workspace"] = protectedWorkspace.Protected.ToString().ToLowerInvariant();
        if (!protectedWorkspace.Protected)
        {
            return Failure(protectedWorkspace.Code, diagnostics);
        }

        var containment = ProjectFileSelectionGuard.CheckWorkspaceContainment(request.TargetFilePath, request.WorkspacePath);
        diagnostics["workspace_containment"] = containment.Code;
        if (!containment.Succeeded)
        {
            return Failure(containment.Code, diagnostics);
        }

        var intake = PlainTextAttachmentIntake.ReadFile(request.TargetFilePath, expectedSourceId);
        diagnostics["file_intake"] = intake.Code;
        if (!intake.Succeeded)
        {
            return Failure(intake.Code, diagnostics, intake.Warnings);
        }

        var currentContentHash = RawFreeIdentity.HashBytes(Encoding.UTF8.GetBytes(intake.ContentPart.RawText));
        diagnostics["current_content_hash"] = currentContentHash;
        if (!string.Equals(currentContentHash, request.SourceFile.ContentHash, StringComparison.Ordinal))
        {
            return Failure("source_version_mismatch", diagnostics);
        }

        var restoreRequest = CreateScopedRestoreRequest(request.SourceFile, request.SanitizedEdit, diagnostics);
        if (restoreRequest is null)
        {
            return Failure("unrelated_pseudonym_in_patch", diagnostics);
        }

        var restoration = _restore(restoreRequest);
        diagnostics["local_sensitive"] = restoration.Metadata.LocalSensitive.ToString().ToLowerInvariant();
        diagnostics["restored_count"] = restoration.Metadata.RestoredPseudonymCountsByType.Values
            .Sum()
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var item in restoration.Metadata.RestoredPseudonymCountsByType.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            diagnostics[$"restored_count.{item.Key}"] = item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new ProjectFilePatchDryRunResult(
            Succeeded: true,
            Code: "restore_aware_patch_preview_ready",
            PreviewText: restoration.Text,
            LocalSensitive: restoration.Metadata.LocalSensitive,
            Warnings: restoration.Warnings,
            Diagnostics: diagnostics);
    }

    private static RestoreRequest? CreateScopedRestoreRequest(
        SanitizedVirtualFile sourceFile,
        string sanitizedEdit,
        Dictionary<string, string> diagnostics)
    {
        var sourcePlaceholders = sourceFile.Replacements
            .Where(replacement => replacement.Restorable)
            .Select(replacement => replacement.Placeholder)
            .ToHashSet(StringComparer.Ordinal);
        var discovered = LocalRestoreWorkflow.DiscoverReplacements(sanitizedEdit);
        var unrelatedRestorableCount = discovered.Count(replacement =>
            replacement.Restorable
            && !sourcePlaceholders.Contains(replacement.Placeholder));
        diagnostics["source_restorable_placeholder_count"] = sourcePlaceholders.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        diagnostics["unrelated_restorable_placeholder_count"] = unrelatedRestorableCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (unrelatedRestorableCount > 0)
        {
            return null;
        }

        return new RestoreRequest(
            SanitizedText: sanitizedEdit,
            Replacements: discovered
                .Where(replacement => !replacement.Restorable || sourcePlaceholders.Contains(replacement.Placeholder))
                .ToArray());
    }

    private static ProjectFilePatchDryRunResult Failure(
        string code,
        Dictionary<string, string> diagnostics,
        IReadOnlyList<Warning>? warnings = null)
    {
        return new ProjectFilePatchDryRunResult(
            Succeeded: false,
            Code: code,
            PreviewText: null,
            LocalSensitive: false,
            Warnings: warnings ?? new[]
            {
                new Warning(code, "Patch dry-run could not be validated for the protected workspace.", WarningSeverity.Error)
            },
            Diagnostics: diagnostics);
    }
}

public sealed class ProjectFilePatchApplier
{
    private readonly ProjectFilePatchDryRun _dryRun;
    private readonly IAuditSink _auditSink;

    public ProjectFilePatchApplier(ProjectFilePatchDryRun dryRun, IAuditSink auditSink)
    {
        _dryRun = dryRun ?? throw new ArgumentNullException(nameof(dryRun));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    }

    public ProjectFilePatchApplyResult Apply(ProjectFilePatchApplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Approved)
        {
            return CompleteWithoutWrite(
                "project_patch_apply_cancelled",
                request.DryRunRequest,
                localSensitive: false,
                warnings: new[]
                {
                    new Warning(
                        "project_patch_apply_cancelled",
                        "Project file patch was not approved for local write.",
                        WarningSeverity.Info)
                });
        }

        var preview = _dryRun.Preview(request.DryRunRequest);
        if (!preview.Succeeded || preview.PreviewText is null)
        {
            return CompleteWithoutWrite(preview.Code, request.DryRunRequest, preview.LocalSensitive, preview.Warnings, preview.Diagnostics);
        }

        var diagnostics = CreateBaseDiagnostics(request.DryRunRequest);
        foreach (var item in preview.Diagnostics)
        {
            diagnostics[item.Key] = item.Value;
        }

        diagnostics["write_performed"] = "true";
        diagnostics["raw_restored_patch_recorded"] = "false";
        diagnostics["restored_patch_hash"] = RawFreeIdentity.HashBytes(Encoding.UTF8.GetBytes(preview.PreviewText));

        var writeContainment = ProjectFileSelectionGuard.CheckWorkspaceContainment(
            request.DryRunRequest.TargetFilePath,
            request.DryRunRequest.WorkspacePath);
        diagnostics["write_workspace_containment"] = writeContainment.Code;
        if (!writeContainment.Succeeded)
        {
            return CompleteWithoutWrite(
                writeContainment.Code,
                request.DryRunRequest,
                preview.LocalSensitive,
                new[] { new Warning(writeContainment.Code, "Project file target escaped the protected workspace before write.", WarningSeverity.Error) },
                diagnostics);
        }

        try
        {
            AtomicFileWriter.WriteAllBytes(
                request.DryRunRequest.TargetFilePath,
                Encoding.UTF8.GetBytes(preview.PreviewText));
        }
        catch (Exception) when (
            OperatingSystem.IsWindows()
            || OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS())
        {
            return CompleteWithoutWrite(
                "project_patch_write_failed",
                request.DryRunRequest,
                preview.LocalSensitive,
                new[] { new Warning("project_patch_write_failed", "Project file patch could not be written.", WarningSeverity.Error) },
                diagnostics);
        }

        var auditWrite = _auditSink.Write(CreateAuditEvent("project_patch_applied", preview.LocalSensitive, diagnostics, preview.Warnings));
        return new ProjectFilePatchApplyResult(
            Succeeded: auditWrite.Succeeded,
            Code: auditWrite.Succeeded ? "project_patch_applied" : "project_patch_audit_failed",
            Written: true,
            LocalSensitive: preview.LocalSensitive,
            AuditWriteResult: auditWrite,
            Warnings: auditWrite.Succeeded
                ? preview.Warnings
                : preview.Warnings.Concat(new[] { new Warning(auditWrite.WarningCode ?? "audit_write_failed", "Project file write audit could not be written.", WarningSeverity.Error) }).ToArray(),
            Diagnostics: diagnostics);
    }

    private ProjectFilePatchApplyResult CompleteWithoutWrite(
        string code,
        ProjectFilePatchDryRunRequest request,
        bool localSensitive,
        IReadOnlyList<Warning> warnings,
        IReadOnlyDictionary<string, string>? extraDiagnostics = null)
    {
        var diagnostics = CreateBaseDiagnostics(request);
        diagnostics["write_performed"] = "false";
        diagnostics["raw_restored_patch_recorded"] = "false";
        if (extraDiagnostics is not null)
        {
            foreach (var item in extraDiagnostics)
            {
                diagnostics[item.Key] = item.Value;
            }
        }

        var auditWrite = _auditSink.Write(CreateAuditEvent(code, localSensitive, diagnostics, warnings));
        return new ProjectFilePatchApplyResult(
            Succeeded: false,
            Code: code,
            Written: false,
            LocalSensitive: localSensitive,
            AuditWriteResult: auditWrite,
            Warnings: warnings,
            Diagnostics: diagnostics);
    }

    private static Dictionary<string, string> CreateBaseDiagnostics(ProjectFilePatchDryRunRequest request)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace_id"] = RawFreeIdentity.HashPath(request.WorkspacePath),
            ["source_id"] = RawFreeIdentity.HashPath(request.TargetFilePath),
            ["expected_content_hash"] = request.SourceFile.ContentHash,
            ["target_file_path_recorded"] = "false"
        };
    }

    private static AuditEvent CreateAuditEvent(
        string code,
        bool localSensitive,
        IReadOnlyDictionary<string, string> diagnostics,
        IReadOnlyList<Warning> warnings)
    {
        var actionCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [code] = 1,
            ["local_sensitive"] = localSensitive ? 1 : 0
        };
        if (diagnostics.TryGetValue("restored_count", out var restoredCount)
            && int.TryParse(restoredCount, out var restored))
        {
            actionCounts["restored_count"] = restored;
        }

        return new AuditEvent(
            Timestamp: DateTimeOffset.UtcNow,
            RequestId: Guid.NewGuid().ToString("N"),
            Application: "project_file_broker",
            WorkspaceHash: diagnostics.GetValueOrDefault("workspace_id"),
            PolicyProfile: "project_file_write",
            Decision: code == "project_patch_applied" ? SanitizeDecision.Allow : SanitizeDecision.Block,
            ScannerStatuses: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["project_file_write"] = code,
                ["raw_restored_patch_recorded"] = "false",
                ["source_id"] = diagnostics.GetValueOrDefault("source_id") ?? "unknown",
                ["expected_content_hash"] = diagnostics.GetValueOrDefault("expected_content_hash") ?? "unknown",
                ["current_content_hash"] = diagnostics.GetValueOrDefault("current_content_hash") ?? "unknown",
                ["restored_patch_hash"] = diagnostics.GetValueOrDefault("restored_patch_hash") ?? "none",
                ["target_file_path_recorded"] = "false"
            },
            EntityCountsByType: new Dictionary<string, int>(StringComparer.Ordinal),
            ActionCounts: actionCounts,
            SpanSummaries: Array.Empty<SpanSummary>(),
            ReplacementSummaries: Array.Empty<ReplacementSummary>(),
            Warnings: warnings,
            AdapterMode: "project_file_broker_write",
            DurationsMs: new Dictionary<string, long>(StringComparer.Ordinal));
    }
}

public static class ProjectFileBypassGuard
{
    public static ProjectFileBypassResult ReportDirectAttachment(
        DefaultStorageLayout layout,
        string workspacePath,
        ProjectFileBypassPolicy? policy = null)
    {
        return Report("direct_attachment", layout, workspacePath, policy);
    }

    public static ProjectFileBypassResult ReportUnmanagedConnector(
        DefaultStorageLayout layout,
        string workspacePath,
        ProjectFileBypassPolicy? policy = null)
    {
        return Report("unmanaged_connector", layout, workspacePath, policy);
    }

    private static ProjectFileBypassResult Report(
        string channel,
        DefaultStorageLayout layout,
        string workspacePath,
        ProjectFileBypassPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var activePolicy = policy ?? ProjectFileBypassPolicy.BrokerOnlyDefault;
        var ingress = ProjectFileIngressStatusInspector.Inspect(layout, workspacePath);
        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workspace_id"] = ingress.WorkspaceId,
            ["protected_workspace"] = ingress.MustBlockUnroutedContext.ToString().ToLowerInvariant(),
            ["precloud_ingress_boundary"] = ingress.Status,
            ["channel"] = channel,
            ["broker_routed"] = "false",
            ["broker_only_file_context_required"] = activePolicy.RequireBrokerOnlyFileContext.ToString().ToLowerInvariant(),
            ["raw_file_path_recorded"] = "false",
            ["raw_file_content_recorded"] = "false"
        };
        var blocked = ingress.MustBlockUnroutedContext && activePolicy.RequireBrokerOnlyFileContext;
        return new ProjectFileBypassResult(
            Allowed: !blocked,
            Code: blocked ? ingress.Code : "workspace_not_protected",
            Warnings: new[]
            {
                new Warning(
                    blocked ? ingress.Code : "workspace_not_protected",
                    blocked
                        ? "Project-file cloud ingress is unsupported for this protected workspace."
                        : "Project file context is not broker-routed.",
                    blocked ? WarningSeverity.Error : WarningSeverity.Warning)
            },
            Diagnostics: diagnostics);
    }
}

public static class ProjectFileProductSmokeRunner
{
    public static ProjectFileProductSmokeReport Run(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-project-file-product-smoke", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            ProtectedWorkspaceStore.Protect(layout, workspace);

            var filePath = Path.Combine(workspace, "config.txt");
            File.WriteAllText(filePath, "Connect to deploy.corp.example.local\r\nRead C:\\Users\\user1\\secret.txt\r\npassword=P@ssw0rd!");
            var unsupportedPath = Path.Combine(workspace, "archive.zip");
            File.WriteAllText(unsupportedPath, "not a supported project file");

            var vault = new InMemoryHmacMappingVault(hmacSecret);
            var broker = new ProjectFileContextBroker(
                new Sanitizer(vault),
                layout,
                ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
            var virtualFileResult = broker.CreateSanitizedVirtualFile(filePath, workspace);
            var virtualFile = virtualFileResult.VirtualFile;
            var readPassed = virtualFileResult.Succeeded
                && virtualFile is not null
                && !virtualFile.SanitizedText.Contains("deploy.corp.example.local", StringComparison.Ordinal)
                && !virtualFile.SanitizedText.Contains("user1", StringComparison.Ordinal)
                && !virtualFile.SanitizedText.Contains("P@ssw0rd!", StringComparison.Ordinal);
            var toolOutput = broker.SanitizeManagedToolOutput(
                "cat returned deploy.corp.example.local and password=P@ssw0rd!",
                workspace,
                "product-smoke-tool");
            var toolPassed = toolOutput.Succeeded
                && toolOutput.ToolOutput is not null
                && !toolOutput.ToolOutput.SanitizedText.Contains("deploy.corp.example.local", StringComparison.Ordinal)
                && !toolOutput.ToolOutput.SanitizedText.Contains("P@ssw0rd!", StringComparison.Ordinal);
            var unsupported = broker.CreateSanitizedVirtualFile(unsupportedPath, workspace);
            var bypass = ProjectFileBypassGuard.ReportDirectAttachment(layout, workspace);

            var restoreWorkflow = new LocalRestoreWorkflow(
                new LocalRestorer(vault),
                new FileAuditSink(Path.Combine(tempDirectory, "restore-audit")));
            var applier = new ProjectFilePatchApplier(
                new ProjectFilePatchDryRun(request => restoreWorkflow.Restore(request).Restoration, layout),
                new FileAuditSink(layout.AuditDirectory));
            var apply = virtualFile is null
                ? null
                : applier.Apply(new ProjectFilePatchApplyRequest(
                    new ProjectFilePatchDryRunRequest(
                        virtualFile,
                        workspace,
                        filePath,
                        virtualFile.SanitizedText.Replace("Connect", "Route", StringComparison.Ordinal)),
                    Approved: true));
            var writtenText = File.ReadAllText(filePath);
            var approvedWritePassed = apply?.Succeeded == true
                && apply.Written
                && apply.LocalSensitive
                && writtenText.Contains("Route to deploy.corp.example.local", StringComparison.Ordinal)
                && writtenText.Contains("PASSWORD_REDACTED", StringComparison.Ordinal)
                && !writtenText.Contains("P@ssw0rd!", StringComparison.Ordinal);
            var auditRecords = AuditChainReader.ReadRecords(layout.AuditDirectory);
            var auditPayload = string.Join(Environment.NewLine, auditRecords.Select(record => AuditChainReader.SerializeEvent(record.Event)));
            var rawFreeAudit = auditRecords.Count > 0
                && !auditPayload.Contains("deploy.corp.example.local", StringComparison.Ordinal)
                && !auditPayload.Contains("user1", StringComparison.Ordinal)
                && !auditPayload.Contains("P@ssw0rd!", StringComparison.Ordinal)
                && !auditPayload.Contains(filePath, StringComparison.Ordinal);
            var brokerWorkflowPassed = readPassed
                && toolPassed
                && approvedWritePassed
                && unsupported.Code == "unsupported_attachment_type"
                && !bypass.Allowed
                && rawFreeAudit;
            var passed = readPassed
                && toolPassed
                && approvedWritePassed
                && unsupported.Code == "unsupported_attachment_type"
                && !bypass.Allowed
                && brokerWorkflowPassed
                && rawFreeAudit;

            return new ProjectFileProductSmokeReport(
                Passed: passed,
                ReadSanitizedVirtualFilePassed: readPassed,
                ToolOutputSanitizedPassed: toolPassed,
                ApprovedWritePassed: approvedWritePassed,
                UnsupportedFileBlockedPassed: unsupported.Code == "unsupported_attachment_type",
                BypassBlockedPassed: !bypass.Allowed,
                BrokerWorkflowPassed: brokerWorkflowPassed,
                RawFreeAuditEvidencePassed: rawFreeAudit,
                AuditEventCount: auditRecords.Count,
                ReplacementCount: virtualFile?.ReplacementCount ?? 0);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    public static IReadOnlyList<string> RenderRawFree(ProjectFileProductSmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new[]
        {
            $"project_file_product_smoke: {(report.Passed ? "passed" : "failed")}",
            $"read_sanitized_virtual_file: {report.ReadSanitizedVirtualFilePassed.ToString().ToLowerInvariant()}",
            $"tool_output_sanitized: {report.ToolOutputSanitizedPassed.ToString().ToLowerInvariant()}",
            $"approved_write: {report.ApprovedWritePassed.ToString().ToLowerInvariant()}",
            $"unsupported_file_blocked: {report.UnsupportedFileBlockedPassed.ToString().ToLowerInvariant()}",
            $"bypass_blocked: {report.BypassBlockedPassed.ToString().ToLowerInvariant()}",
            $"project_file_broker_workflow: {report.BrokerWorkflowPassed.ToString().ToLowerInvariant()}",
            "project_files_protected: false",
            $"raw_free_audit_evidence: {report.RawFreeAuditEvidencePassed.ToString().ToLowerInvariant()}",
            $"audit_events: {report.AuditEventCount}",
            $"replacement_count: {report.ReplacementCount}"
        };
    }
}

public sealed record ProjectFileReadOnlySmokeReport(
    bool Passed,
    bool WorkspaceRegistered,
    bool ReadSucceeded,
    bool PayloadSanitized,
    bool RawFreeEvidence,
    bool LiveProjectFilesProtected,
    int ReplacementCount,
    string StatusCode);

public static class ProjectFileReadOnlySmokeRunner
{
    public static ProjectFileReadOnlySmokeReport Run(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "codex-redaction-gate-project-file-smoke", Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = Path.Combine(tempDirectory, "workspace");
            Directory.CreateDirectory(workspace);
            var filePath = Path.Combine(workspace, "config.txt");
            File.WriteAllText(filePath, "Connect to deploy.corp.example.local\r\nRead C:\\Users\\user1\\secret.txt\r\npassword=P@ssw0rd!");
            var layout = DefaultStorageLayout.Create(Path.Combine(tempDirectory, "data"));
            var registration = ProtectedWorkspaceStore.Protect(layout, workspace);
            var broker = new ProjectFileContextBroker(
                new Sanitizer(new InMemoryHmacMappingVault(hmacSecret)),
                layout,
                ProjectFileBrokerOptions.ProtectedWorkspaceDefault);
            var result = broker.CreateSanitizedVirtualFile(filePath, workspace);
            var sanitized = result.VirtualFile?.SanitizedText ?? string.Empty;
            var evidence = string.Join(Environment.NewLine, RenderRawFree(result));
            var payloadSanitized = !sanitized.Contains("deploy.corp.example.local", StringComparison.Ordinal)
                && !sanitized.Contains("user1", StringComparison.Ordinal)
                && !sanitized.Contains("P@ssw0rd!", StringComparison.Ordinal)
                && sanitized.Contains("PASSWORD_REDACTED", StringComparison.Ordinal);
            var rawFreeEvidence = !evidence.Contains("deploy.corp.example.local", StringComparison.Ordinal)
                && !evidence.Contains("user1", StringComparison.Ordinal)
                && !evidence.Contains("P@ssw0rd!", StringComparison.Ordinal)
                && !evidence.Contains(filePath, StringComparison.Ordinal)
                && !evidence.Contains(workspace, StringComparison.Ordinal);
            var liveProjectFilesProtected = false;
            var passed = registration.Succeeded
                && result.Succeeded
                && payloadSanitized
                && rawFreeEvidence
                && !liveProjectFilesProtected;

            return new ProjectFileReadOnlySmokeReport(
                passed,
                registration.Succeeded,
                result.Succeeded,
                payloadSanitized,
                rawFreeEvidence,
                liveProjectFilesProtected,
                result.VirtualFile?.ReplacementCount ?? 0,
                result.Code);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    public static IReadOnlyList<string> RenderRawFree(ProjectFileReadOnlySmokeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new[]
        {
            $"project_file_smoke: {(report.Passed ? "passed" : "failed")}",
            $"workspace_registered: {report.WorkspaceRegistered.ToString().ToLowerInvariant()}",
            $"read_succeeded: {report.ReadSucceeded.ToString().ToLowerInvariant()}",
            $"payload_sanitized: {report.PayloadSanitized.ToString().ToLowerInvariant()}",
            $"raw_free_evidence: {report.RawFreeEvidence.ToString().ToLowerInvariant()}",
            $"live_project_files_protected: {report.LiveProjectFilesProtected.ToString().ToLowerInvariant()}",
            $"replacement_count: {report.ReplacementCount}",
            $"status_code: {report.StatusCode}"
        };
    }

    private static IReadOnlyList<string> RenderRawFree(ProjectFileBrokerResult result)
    {
        var lines = new List<string>
        {
            $"status: {result.Code}",
            $"succeeded: {result.Succeeded.ToString().ToLowerInvariant()}"
        };
        foreach (var item in result.Diagnostics.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            lines.Add($"{item.Key}: {item.Value}");
        }

        lines.Add($"replacement_count: {result.VirtualFile?.ReplacementCount ?? 0}");
        return lines;
    }
}

internal static class ProjectFileSelectionGuard
{
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0x0;

    public static ProjectFileSelectionResult CheckWorkspaceContainment(string filePath, string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var normalizedFile = ResolveFinalPath(filePath, isDirectory: false);
        var normalizedWorkspace = ResolveFinalPath(workspacePath, isDirectory: true);
        var workspaceWithSeparator = normalizedWorkspace.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedWorkspace
            : normalizedWorkspace + Path.DirectorySeparatorChar;
        var inside = normalizedFile.StartsWith(workspaceWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || string.Equals(normalizedFile, normalizedWorkspace, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        return inside
            ? new ProjectFileSelectionResult(true, "inside_workspace")
            : new ProjectFileSelectionResult(false, "outside_workspace");
    }

    private static string ResolveFinalPath(string path, bool isDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        using var handle = CreateFileW(
            fullPath,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            isDirectory ? (FileAttributes)FileFlagBackupSemantics : FileAttributes.Normal,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return fullPath;
        }

        var builder = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, FileNameNormalized);
        if (length == 0)
        {
            return fullPath;
        }

        if (length >= builder.Capacity)
        {
            builder.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, FileNameNormalized);
            if (length == 0)
            {
                return fullPath;
            }
        }

        return StripWin32PathPrefix(builder.ToString());
    }

    private static string StripWin32PathPrefix(string path)
    {
        const string extendedPathPrefix = @"\\?\";
        const string extendedUncPrefix = @"\\?\UNC\";

        if (path.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUncPrefix.Length..];
        }

        return path.StartsWith(extendedPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPathPrefix.Length..]
            : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        FileAttributes dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}

internal sealed record ProjectFileSelectionResult(bool Succeeded, string Code);

internal static class RawFreeIdentity
{
    public static string HashPath(string path)
    {
        return "file_" + HashString(Path.GetFullPath(path)).Substring(0, 16);
    }

    public static string HashBytes(byte[] bytes)
    {
        return HashToHex(SHA256.HashData(bytes)).Substring(0, 16);
    }

    public static string HashString(string value)
    {
        return HashToHex(SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 16);
    }

    private static string HashToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
