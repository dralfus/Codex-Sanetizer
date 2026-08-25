using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexRedactionGate;

internal sealed record ProtectionOperationEvent(
    long Sequence,
    DateTimeOffset Timestamp,
    string Source,
    string Action,
    string Stage,
    string Status,
    string ResultCode,
    long AttemptId = 0);

internal sealed class ProtectionOperationJournal
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;

    public ProtectionOperationJournal(DefaultStorageLayout layout, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Path = System.IO.Path.Combine(layout.RootDirectory, "logs", "protection-operations.jsonl");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _sequence = ReadRecent(1).LastOrDefault()?.Sequence ?? 0;
    }

    internal string Path { get; }

    public void Append(
        string source,
        string action,
        string stage,
        string status,
        string resultCode,
        long attemptId = 0)
    {
        var entry = new ProtectionOperationEvent(
            Sequence: 0,
            Timestamp: _clock(),
            Source: SafeToken(source),
            Action: SafeToken(action),
            Stage: SafeToken(stage),
            Status: SafeToken(status),
            ResultCode: SafeToken(resultCode),
            AttemptId: Math.Max(attemptId, 0));

        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                entry = entry with { Sequence = ++_sequence };
                File.AppendAllText(Path, JsonSerializer.Serialize(entry) + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never break the fail-closed resident workflow.
        }
    }

    public IReadOnlyList<ProtectionOperationEvent> ReadRecent(int count)
    {
        if (count <= 0 || !File.Exists(Path))
        {
            return Array.Empty<ProtectionOperationEvent>();
        }

        try
        {
            lock (_gate)
            {
                return File.ReadLines(Path)
                    .Select(TryDeserialize)
                    .Where(item => item is not null)
                    .Cast<ProtectionOperationEvent>()
                    .TakeLast(count)
                    .ToArray();
            }
        }
        catch
        {
            return Array.Empty<ProtectionOperationEvent>();
        }
    }

    private static ProtectionOperationEvent? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<ProtectionOperationEvent>(line);
        }
        catch
        {
            return null;
        }
    }

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return new string(value
            .Take(96)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'
                ? character
                : '_')
            .ToArray())
            .ToLower(CultureInfo.InvariantCulture);
    }
}
