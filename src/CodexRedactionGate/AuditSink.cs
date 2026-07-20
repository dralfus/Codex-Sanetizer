using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexRedactionGate;

public sealed record AuditWriteResult(bool Succeeded, string? WarningCode)
{
    public static AuditWriteResult Success { get; } = new(true, null);

    public static AuditWriteResult Failure(string warningCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(warningCode);
        return new AuditWriteResult(false, warningCode);
    }
}

public interface IAuditSink
{
    AuditWriteResult Write(AuditEvent auditEvent);
}

public sealed record FileAuditSinkOptions(int MaxEvents)
{
    public static FileAuditSinkOptions Default { get; } = new(MaxEvents: 1000);
}

public sealed class FileAuditSink : IAuditSink
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly string _auditDirectory;
    private readonly FileAuditSinkOptions _options;

    public FileAuditSink(string auditDirectory, FileAuditSinkOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditDirectory);

        _auditDirectory = Path.GetFullPath(auditDirectory);
        _options = options ?? FileAuditSinkOptions.Default;
    }

    public static FileAuditSink CreateDefault(FileAuditSinkOptions? options = null)
    {
        return new FileAuditSink(DefaultStorageLayout.CreateDefault().AuditDirectory, options);
    }

    public AuditWriteResult Write(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        try
        {
            Directory.CreateDirectory(_auditDirectory);
            var fileName = $"audit-{auditEvent.Timestamp:yyyyMMddHHmmssfffffff}-{auditEvent.RequestId}.json";
            var path = Path.Combine(_auditDirectory, fileName);
            var eventPayload = JsonSerializer.Serialize(auditEvent, SerializerOptions);
            var previousHash = AuditChainReader.ReadLastHash(_auditDirectory);
            var hash = AuditChainReader.ComputeHash(previousHash, eventPayload);
            var record = new PersistedAuditRecord(previousHash, hash, auditEvent);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, SerializerOptions));

            AtomicFileWriter.WriteAllBytes(path, bytes);
            ApplyRetention();
            AuditChainReader.RebuildChain(_auditDirectory);
            AuditChainReader.WriteHead(_auditDirectory);
            return AuditWriteResult.Success;
        }
        catch (Exception) when (
            OperatingSystem.IsWindows()
            || OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS())
        {
            return AuditWriteResult.Failure("audit_write_failed");
        }
    }

    private void ApplyRetention()
    {
        if (_options.MaxEvents <= 0)
        {
            return;
        }

        var files = new DirectoryInfo(_auditDirectory)
            .EnumerateFiles("audit-*.json")
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name)
            .Skip(_options.MaxEvents)
            .ToArray();

        foreach (var file in files)
        {
            file.Delete();
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record PersistedAuditRecord(
    [property: JsonPropertyName("previous_hash")] string PreviousHash,
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("event")] AuditEvent Event);

public sealed record PersistedAuditChainHead(
    [property: JsonPropertyName("last_hash")] string LastHash,
    [property: JsonPropertyName("event_count")] int EventCount);

public sealed record AuditChainVerificationResult(
    bool Valid,
    string Code,
    int EventCount);

public sealed record AuditSummaryReport(
    AuditChainVerificationResult Chain,
    IReadOnlyDictionary<string, int> DecisionCounts,
    IReadOnlyDictionary<string, int> WarningCodeCounts,
    DateTimeOffset? FirstEvent,
    DateTimeOffset? LastEvent);

public static class AuditChainVerifier
{
    public static AuditChainVerificationResult Verify(string auditDirectory)
    {
        var records = AuditChainReader.ReadRecords(auditDirectory);
        var previousHash = string.Empty;

        foreach (var record in records)
        {
            var eventPayload = AuditChainReader.SerializeEvent(record.Event);
            var expectedHash = AuditChainReader.ComputeHash(previousHash, eventPayload);

            if (!string.Equals(record.PreviousHash, previousHash, StringComparison.Ordinal))
            {
                return new AuditChainVerificationResult(false, "audit_chain_link_mismatch", records.Count);
            }

            if (!string.Equals(record.Hash, expectedHash, StringComparison.Ordinal))
            {
                return new AuditChainVerificationResult(false, "audit_chain_hash_mismatch", records.Count);
            }

            previousHash = record.Hash;
        }

        var head = AuditChainReader.ReadHead(auditDirectory);
        if (head is not null
            && (head.EventCount != records.Count
                || !string.Equals(head.LastHash, previousHash, StringComparison.Ordinal)))
        {
            return new AuditChainVerificationResult(false, "audit_chain_head_mismatch", records.Count);
        }

        return new AuditChainVerificationResult(true, "audit_chain_valid", records.Count);
    }
}

public static class AuditSummaryReporter
{
    public static AuditSummaryReport Summarize(string auditDirectory)
    {
        var records = AuditChainReader.ReadRecords(auditDirectory);
        var events = records.Select(record => record.Event).ToArray();

        return new AuditSummaryReport(
            Chain: AuditChainVerifier.Verify(auditDirectory),
            DecisionCounts: events
                .GroupBy(auditEvent => auditEvent.Decision.ToString())
                .ToDictionary(group => group.Key, group => group.Count()),
            WarningCodeCounts: events
                .SelectMany(auditEvent => auditEvent.Warnings)
                .GroupBy(warning => warning.Code)
                .ToDictionary(group => group.Key, group => group.Count()),
            FirstEvent: events.Length == 0 ? null : events.Min(auditEvent => auditEvent.Timestamp),
            LastEvent: events.Length == 0 ? null : events.Max(auditEvent => auditEvent.Timestamp));
    }
}

internal static class AuditChainReader
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static IReadOnlyList<PersistedAuditRecord> ReadRecords(string auditDirectory)
    {
        if (!Directory.Exists(auditDirectory))
        {
            return Array.Empty<PersistedAuditRecord>();
        }

        return new DirectoryInfo(auditDirectory)
            .EnumerateFiles("audit-*.json")
            .OrderBy(file => file.Name)
            .Select(file => JsonSerializer.Deserialize<PersistedAuditRecord>(File.ReadAllText(file.FullName), SerializerOptions))
            .Where(record => record is not null)
            .Cast<PersistedAuditRecord>()
            .ToArray();
    }

    public static string ReadLastHash(string auditDirectory)
    {
        return ReadRecords(auditDirectory).LastOrDefault()?.Hash ?? string.Empty;
    }

    public static PersistedAuditChainHead? ReadHead(string auditDirectory)
    {
        var headPath = Path.Combine(auditDirectory, "chain-head.json");
        return File.Exists(headPath)
            ? JsonSerializer.Deserialize<PersistedAuditChainHead>(File.ReadAllText(headPath), SerializerOptions)
            : null;
    }

    public static void WriteHead(string auditDirectory)
    {
        var records = ReadRecords(auditDirectory);
        var head = new PersistedAuditChainHead(
            LastHash: records.LastOrDefault()?.Hash ?? string.Empty,
            EventCount: records.Count);
        AtomicFileWriter.WriteAllBytes(
            Path.Combine(auditDirectory, "chain-head.json"),
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(head, SerializerOptions)));
    }

    public static void RebuildChain(string auditDirectory)
    {
        var records = ReadRecords(auditDirectory);
        var previousHash = string.Empty;

        foreach (var record in records)
        {
            var eventPayload = SerializeEvent(record.Event);
            var hash = ComputeHash(previousHash, eventPayload);
            var rebuilt = new PersistedAuditRecord(previousHash, hash, record.Event);
            var fileName = $"audit-{record.Event.Timestamp:yyyyMMddHHmmssfffffff}-{record.Event.RequestId}.json";
            AtomicFileWriter.WriteAllBytes(
                Path.Combine(auditDirectory, fileName),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rebuilt, SerializerOptions)));
            previousHash = hash;
        }
    }

    public static string SerializeEvent(AuditEvent auditEvent)
    {
        return JsonSerializer.Serialize(auditEvent, SerializerOptions);
    }

    public static string ComputeHash(string previousHash, string eventPayload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{previousHash}\n{eventPayload}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
