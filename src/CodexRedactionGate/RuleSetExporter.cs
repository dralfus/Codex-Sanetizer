using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record RuleSetExportResult(
    bool Succeeded,
    string Code,
    IReadOnlyList<string> ExportedFiles);

public static class RuleSetExporter
{
    public static RuleSetExportResult Export(DefaultStorageLayout layout, string exportDirectory)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);

        var targetDirectory = Path.GetFullPath(exportDirectory);
        Directory.CreateDirectory(targetDirectory);

        var sources = new[]
        {
            PolicyActivationStore.ActivePolicyPath(layout.PolicyDirectory),
            ManagedSensitiveDictionary.DefaultPath(layout)
        };
        var exported = new List<string>();

        foreach (var source in sources.Where(File.Exists))
        {
            var destination = Path.Combine(targetDirectory, Path.GetFileName(source));
            File.Copy(source, destination, overwrite: true);
            exported.Add(Path.GetFileName(destination));
        }

        return new RuleSetExportResult(true, "rules_exported", exported);
    }
}
