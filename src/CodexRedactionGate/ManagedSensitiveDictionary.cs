using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexRedactionGate;

public sealed record ManagedDictionaryEntry(
    string Id,
    string Type,
    string Value,
    string Action,
    string? Notes);

public sealed record ManagedDictionarySummary(
    string Id,
    string Type,
    string Action,
    int ValueLength,
    string? Notes);

public sealed record ManagedDictionaryMutationResult(
    bool Succeeded,
    string Code,
    string? EntryId);

public sealed record ManagedDictionaryBatchItemResult(
    bool Succeeded,
    string Code,
    string Type,
    int ValueLength,
    string? EntryId);

public sealed record ManagedDictionaryBatchResult(
    bool Succeeded,
    string Code,
    IReadOnlyList<ManagedDictionaryBatchItemResult> Items);

public sealed record ManagedDictionaryRemoveItemResult(
    bool Succeeded,
    string Code,
    string EntryId);

public sealed record ManagedDictionaryRemoveBatchResult(
    bool Succeeded,
    string Code,
    IReadOnlyList<ManagedDictionaryRemoveItemResult> Items);

public sealed class ManagedSensitiveDictionary
{
    private static readonly string Header = "id,type,value,action,notes";
    private readonly string _filePath;

    public ManagedSensitiveDictionary(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public static string DefaultPath(DefaultStorageLayout? layout = null)
    {
        var resolvedLayout = layout ?? DefaultStorageLayout.CreateDefault();
        return Path.Combine(resolvedLayout.PolicyDirectory, "managed-dictionary.csv");
    }

    public static IReadOnlyList<DictionaryTerm> LoadDefaultTerms()
    {
        return new ManagedSensitiveDictionary(DefaultPath()).LoadTerms();
    }

    public ManagedDictionaryMutationResult Add(string type, string value, string? notes = null)
    {
        var result = AddBatch(new[]
        {
            new DictionaryTerm(type, value, PolicyActions.PseudonymizeRestorable, notes)
        });
        var item = result.Items.SingleOrDefault();
        return result.Succeeded
            ? new ManagedDictionaryMutationResult(true, "dictionary_term_added", item?.EntryId)
            : new ManagedDictionaryMutationResult(false, item?.Code ?? result.Code, null);
    }

    public ManagedDictionaryBatchResult AddBatch(IReadOnlyList<DictionaryTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0)
        {
            return new ManagedDictionaryBatchResult(false, "dictionary_batch_empty", Array.Empty<ManagedDictionaryBatchItemResult>());
        }

        var entries = LoadEntries().ToList();
        var existing = entries
            .Select(entry => CreateKey(entry.Type, entry.Value))
            .ToHashSet(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var validation = new List<ManagedDictionaryBatchItemResult>();

        foreach (var term in terms)
        {
            var code = ValidateTerm(term);
            if (code is null)
            {
                var key = CreateKey(term.Type, term.Value!);
                if (existing.Contains(key) || !pending.Add(key))
                {
                    code = "dictionary_term_exists";
                }
            }

            validation.Add(new ManagedDictionaryBatchItemResult(
                Succeeded: code is null,
                Code: code ?? "dictionary_term_valid",
                Type: term.Type,
                ValueLength: term.Value?.Length ?? 0,
                EntryId: null));
        }

        if (validation.Any(item => !item.Succeeded))
        {
            return new ManagedDictionaryBatchResult(false, "dictionary_batch_rejected", validation);
        }

        var added = new List<ManagedDictionaryBatchItemResult>();
        foreach (var term in terms)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            entries.Add(new ManagedDictionaryEntry(
                Id: id,
                Type: term.Type,
                Value: term.Value,
                Action: PolicyActions.PseudonymizeRestorable,
                Notes: term.Notes));
            added.Add(new ManagedDictionaryBatchItemResult(
                Succeeded: true,
                Code: "dictionary_term_added",
                Type: term.Type,
                ValueLength: term.Value.Length,
                EntryId: id));
        }

        Save(entries);
        return new ManagedDictionaryBatchResult(true, "dictionary_batch_added", added);
    }

    public IReadOnlyList<ManagedDictionarySummary> ListSummaries()
    {
        return LoadEntries()
            .Select(entry => new ManagedDictionarySummary(
                entry.Id,
                entry.Type,
                entry.Action,
                entry.Value.Length,
                entry.Notes))
            .ToArray();
    }

    public IReadOnlyList<ManagedDictionaryEntry> ListEntriesForLocalReveal()
    {
        return LoadEntries();
    }

    public ManagedDictionaryMutationResult Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var result = RemoveBatch(new[] { id });
        var item = result.Items.SingleOrDefault();
        return result.Succeeded
            ? new ManagedDictionaryMutationResult(true, "dictionary_term_removed", id)
            : new ManagedDictionaryMutationResult(false, item?.Code ?? result.Code, id);
    }

    public ManagedDictionaryRemoveBatchResult RemoveBatch(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new ManagedDictionaryRemoveBatchResult(false, "dictionary_remove_empty", Array.Empty<ManagedDictionaryRemoveItemResult>());
        }

        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            return new ManagedDictionaryRemoveBatchResult(
                false,
                "dictionary_remove_rejected",
                ids.Select(id => new ManagedDictionaryRemoveItemResult(
                    Succeeded: !string.IsNullOrWhiteSpace(id),
                    Code: string.IsNullOrWhiteSpace(id) ? "invalid_dictionary_id" : "dictionary_id_valid",
                    EntryId: id ?? string.Empty)).ToArray());
        }

        var entries = LoadEntries().ToList();
        var requested = ids.ToHashSet(StringComparer.Ordinal);
        var items = ids
            .Select(id => new ManagedDictionaryRemoveItemResult(
                Succeeded: entries.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal)),
                Code: entries.Any(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
                    ? "dictionary_term_removed"
                    : "dictionary_term_not_found",
                EntryId: id))
            .ToArray();

        if (items.Any(item => !item.Succeeded))
        {
            return new ManagedDictionaryRemoveBatchResult(false, "dictionary_remove_rejected", items);
        }

        var remaining = entries
            .Where(entry => !requested.Contains(entry.Id))
            .ToArray();

        Save(remaining);
        return new ManagedDictionaryRemoveBatchResult(true, "dictionary_terms_removed", items);
    }

    public IReadOnlyList<DictionaryTerm> LoadTerms()
    {
        return LoadEntries()
            .Select(entry => new DictionaryTerm(
                entry.Type,
                entry.Value,
                entry.Action,
                entry.Notes))
            .ToArray();
    }

    public CsvDictionaryLoadResult ImportCsv(string dictionaryFilePath)
    {
        var previousTerms = LoadTerms();
        var result = new CsvDictionaryLoader().LoadOrDefault(dictionaryFilePath, previousTerms);
        if (!result.Activated || !result.LoadedFromFile)
        {
            return result;
        }

        Save(result.ActiveTerms
            .Select(term => new ManagedDictionaryEntry(
                Id: Guid.NewGuid().ToString("N")[..12],
                Type: term.Type,
                Value: term.Value,
                Action: PolicyActions.PseudonymizeRestorable,
                Notes: term.Notes))
            .ToArray());
        return result;
    }

    private IReadOnlyList<ManagedDictionaryEntry> LoadEntries()
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<ManagedDictionaryEntry>();
        }

        var lines = File.ReadAllLines(_filePath);
        if (lines.Length == 0)
        {
            return Array.Empty<ManagedDictionaryEntry>();
        }

        if (!string.Equals(lines[0], Header, StringComparison.Ordinal))
        {
            return Array.Empty<ManagedDictionaryEntry>();
        }

        return lines
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseEntry)
            .Where(entry => SensitiveEntityTypes.IsSupportedDictionaryType(entry.Type)
                && entry.Action == PolicyActions.PseudonymizeRestorable
                && !string.IsNullOrWhiteSpace(entry.Value))
            .ToArray();
    }

    private void Save(IReadOnlyList<ManagedDictionaryEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        foreach (var entry in entries.OrderBy(entry => entry.Type).ThenBy(entry => entry.Id))
        {
            builder.AppendLine(string.Join(
                ',',
                CsvRows.Escape(entry.Id),
                CsvRows.Escape(entry.Type),
                CsvRows.Escape(entry.Value),
                CsvRows.Escape(entry.Action),
                CsvRows.Escape(entry.Notes ?? string.Empty)));
        }

        AtomicFileWriter.WriteAllBytes(_filePath, Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string? ValidateTerm(DictionaryTerm term)
    {
        return !SensitiveEntityTypes.IsSupportedDictionaryType(term.Type)
            || string.IsNullOrWhiteSpace(term.Value)
            || term.Action != PolicyActions.PseudonymizeRestorable
            ? "invalid_dictionary_term"
            : null;
    }

    private static string CreateKey(string type, string value)
    {
        return $"{type}\u001f{value}";
    }

    private static ManagedDictionaryEntry ParseEntry(string line)
    {
        IReadOnlyList<string> fields;
        try
        {
            fields = CsvRows.ParseLine(line);
        }
        catch (FormatException)
        {
            fields = Array.Empty<string>();
        }

        return fields.Count == 5
            ? new ManagedDictionaryEntry(fields[0], fields[1], fields[2], fields[3], string.IsNullOrWhiteSpace(fields[4]) ? null : fields[4])
            : new ManagedDictionaryEntry(string.Empty, string.Empty, string.Empty, string.Empty, null);
    }
}
