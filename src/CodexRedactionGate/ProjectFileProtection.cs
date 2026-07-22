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
        var document = LoadDocument(path);
        var active = document.Workspaces.Any(record =>
            string.Equals(record.WorkspaceId, workspaceId, StringComparison.Ordinal)
            && record.Enabled);
        return new ProtectedWorkspaceStatus(
            active,
            active ? "protected_workspace_registered" : "protected_workspace_not_registered",
            workspaceId,
            path);
    }

    private static ProtectedWorkspaceDocument LoadDocument(string path)
    {
        if (!File.Exists(path))
        {
            return new ProtectedWorkspaceDocument(1, Array.Empty<ProtectedWorkspaceRecord>());
        }

        try
        {
            return JsonSerializer.Deserialize<ProtectedWorkspaceDocument>(File.ReadAllText(path))
                ?? new ProtectedWorkspaceDocument(1, Array.Empty<ProtectedWorkspaceRecord>());
        }
        catch (JsonException)
        {
            return new ProtectedWorkspaceDocument(1, Array.Empty<ProtectedWorkspaceRecord>());
        }
    }

    private static void SaveDocument(string path, ProtectedWorkspaceDocument document)
    {
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        AtomicFileWriter.WriteAllBytes(path, Encoding.UTF8.GetBytes(json));
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
            ["tool_output_managed"] = "true"
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
