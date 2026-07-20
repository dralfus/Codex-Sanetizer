using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodexRedactionGate;

public sealed record DictionaryTerm(
    string Type,
    string Value,
    string Action,
    string? Notes);

public sealed record CsvDictionaryLoadResult(
    IReadOnlyList<DictionaryTerm> ActiveTerms,
    bool LoadedFromFile,
    bool Activated,
    IReadOnlyList<Warning> Warnings);

public sealed class CsvDictionaryLoader
{
    private static readonly string[] ExpectedHeader = { "type", "value", "action", "notes" };
    private static readonly string[] ExpectedManagedHeader = { "id", "type", "value", "action", "notes" };

    public CsvDictionaryLoadResult LoadOrDefault(
        string? dictionaryFilePath,
        IReadOnlyList<DictionaryTerm>? lastKnownGood = null)
    {
        if (string.IsNullOrWhiteSpace(dictionaryFilePath) || !File.Exists(dictionaryFilePath))
        {
            return new CsvDictionaryLoadResult(
                ActiveTerms: lastKnownGood ?? Array.Empty<DictionaryTerm>(),
                LoadedFromFile: false,
                Activated: true,
                Warnings: Array.Empty<Warning>());
        }

        try
        {
            var terms = Parse(File.ReadAllLines(dictionaryFilePath));
            return new CsvDictionaryLoadResult(
                ActiveTerms: terms,
                LoadedFromFile: true,
                Activated: true,
                Warnings: Array.Empty<Warning>());
        }
        catch (Exception exception) when (exception is DictionaryValidationException or IOException or UnauthorizedAccessException)
        {
            return new CsvDictionaryLoadResult(
                ActiveTerms: lastKnownGood ?? Array.Empty<DictionaryTerm>(),
                LoadedFromFile: true,
                Activated: false,
                Warnings: new[]
                {
                    new Warning(
                        Code: "invalid_dictionary_rejected",
                        Message: "CSV dictionary is invalid and was not activated.",
                        Severity: WarningSeverity.Error)
                });
        }
    }

    private static IReadOnlyList<DictionaryTerm> Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            throw new DictionaryValidationException();
        }

        var header = CsvRows.ParseLine(lines[0]);
        var fieldMap = ResolveFieldMap(header);
        if (fieldMap is null)
        {
            throw new DictionaryValidationException();
        }

        var terms = new List<DictionaryTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 1; index < lines.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            IReadOnlyList<string> fields;
            try
            {
                fields = CsvRows.ParseLine(lines[index]);
            }
            catch (FormatException)
            {
                throw new DictionaryValidationException();
            }
            if (fields.Count != header.Count)
            {
                throw new DictionaryValidationException();
            }

            var type = fields[fieldMap.Value.TypeIndex].Trim();
            var value = fields[fieldMap.Value.ValueIndex].Trim();
            var action = fields[fieldMap.Value.ActionIndex].Trim();
            var notes = string.IsNullOrWhiteSpace(fields[fieldMap.Value.NotesIndex])
                ? null
                : fields[fieldMap.Value.NotesIndex].Trim();

            if (!SensitiveEntityTypes.IsSupportedDictionaryType(type)
                || string.IsNullOrWhiteSpace(value)
                || action != PolicyActions.PseudonymizeRestorable)
            {
                throw new DictionaryValidationException();
            }

            if (!seen.Add($"{type}\u001f{value}"))
            {
                throw new DictionaryValidationException();
            }

            terms.Add(new DictionaryTerm(type, value, action, notes));
        }

        return terms;
    }

    private static CsvDictionaryFieldMap? ResolveFieldMap(IReadOnlyList<string> header)
    {
        if (header.SequenceEqual(ExpectedHeader, StringComparer.Ordinal))
        {
            return new CsvDictionaryFieldMap(0, 1, 2, 3);
        }

        if (header.SequenceEqual(ExpectedManagedHeader, StringComparer.Ordinal))
        {
            return new CsvDictionaryFieldMap(1, 2, 3, 4);
        }

        return null;
    }

    private readonly record struct CsvDictionaryFieldMap(
        int TypeIndex,
        int ValueIndex,
        int ActionIndex,
        int NotesIndex);

    private sealed class DictionaryValidationException : Exception
    {
    }
}
