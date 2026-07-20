using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record LocalDataCleanupReport(
    bool Succeeded,
    string Code,
    bool Deleted,
    string RootDirectory,
    IReadOnlyList<string> PlannedDirectories,
    IReadOnlyList<string> DeletedDirectories);

public static class LocalDataCleanup
{
    public const string ConfirmationFlag = "--i-understand-delete-local-sensitive-data";

    public static LocalDataCleanupReport Plan(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new LocalDataCleanupReport(
            Succeeded: true,
            Code: "local_data_kept",
            Deleted: false,
            RootDirectory: layout.RootDirectory,
            PlannedDirectories: SensitiveDirectories(layout),
            DeletedDirectories: Array.Empty<string>());
    }

    public static LocalDataCleanupReport Delete(DefaultStorageLayout layout, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var planned = SensitiveDirectories(layout);
        if (!confirmed)
        {
            return new LocalDataCleanupReport(
                Succeeded: false,
                Code: "cleanup_confirmation_required",
                Deleted: false,
                RootDirectory: layout.RootDirectory,
                PlannedDirectories: planned,
                DeletedDirectories: Array.Empty<string>());
        }

        var root = Path.GetFullPath(layout.RootDirectory);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
        {
            return new LocalDataCleanupReport(
                Succeeded: false,
                Code: "cleanup_unsafe_root",
                Deleted: false,
                RootDirectory: root,
                PlannedDirectories: planned,
                DeletedDirectories: Array.Empty<string>());
        }

        if (!Directory.Exists(root))
        {
            return new LocalDataCleanupReport(
                Succeeded: true,
                Code: "local_data_absent",
                Deleted: false,
                RootDirectory: root,
                PlannedDirectories: planned,
                DeletedDirectories: Array.Empty<string>());
        }

        Directory.Delete(root, recursive: true);
        return new LocalDataCleanupReport(
            Succeeded: true,
            Code: "local_data_deleted",
            Deleted: true,
            RootDirectory: root,
            PlannedDirectories: planned,
            DeletedDirectories: planned);
    }

    private static IReadOnlyList<string> SensitiveDirectories(DefaultStorageLayout layout)
    {
        return new[]
            {
                layout.PolicyDirectory,
                layout.VaultDirectory,
                layout.AuditDirectory,
                layout.SettingsDirectory
            }
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }
}
