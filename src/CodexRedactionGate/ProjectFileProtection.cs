using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    IReadOnlyDictionary<string, int> EntityCountsByType);

public sealed record ProjectFileBrokerResult(
    bool Succeeded,
    string Code,
    SanitizedVirtualFile? VirtualFile,
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
            EntityCountsByType: contentResult.AuditEvent.EntityCountsByType);
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

internal static class ProjectFileSelectionGuard
{
    public static ProjectFileSelectionResult CheckWorkspaceContainment(string filePath, string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var normalizedFile = Path.GetFullPath(filePath);
        var normalizedWorkspace = Path.GetFullPath(workspacePath);
        var workspaceWithSeparator = normalizedWorkspace.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedWorkspace
            : normalizedWorkspace + Path.DirectorySeparatorChar;
        var inside = normalizedFile.StartsWith(workspaceWithSeparator, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || string.Equals(normalizedFile, normalizedWorkspace, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        return inside
            ? new ProjectFileSelectionResult(true, "inside_workspace")
            : new ProjectFileSelectionResult(false, "outside_workspace");
    }
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

    private static string HashString(string value)
    {
        return HashToHex(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string HashToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
