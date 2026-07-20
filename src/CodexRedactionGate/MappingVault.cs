using System;
using System.Collections.Generic;

namespace CodexRedactionGate;

public interface IMappingVault
{
    string GetOrCreatePseudonym(string entityType, string normalizedValue);

    bool TryGetPseudonym(string entityType, string normalizedValue, out string pseudonym);

    bool TryGetOriginal(string pseudonym, out MappingVaultRecord record);
}

public sealed record MappingVaultRecord(string EntityType, string NormalizedValue, string Pseudonym);

public sealed class InMemoryHmacMappingVault : IMappingVault
{
    private readonly byte[] _hmacSecret;
    private readonly Dictionary<string, MappingVaultRecord> _byOriginal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MappingVaultRecord> _byPseudonym = new(StringComparer.Ordinal);

    public InMemoryHmacMappingVault(byte[] hmacSecret)
    {
        ArgumentNullException.ThrowIfNull(hmacSecret);

        if (hmacSecret.Length == 0)
        {
            throw new ArgumentException("HMAC secret must not be empty.", nameof(hmacSecret));
        }

        _hmacSecret = (byte[])hmacSecret.Clone();
    }

    public string GetOrCreatePseudonym(string entityType, string normalizedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValue);

        var key = $"{entityType}\u001f{normalizedValue}";
        if (_byOriginal.TryGetValue(key, out var existing))
        {
            return existing.Pseudonym;
        }

        var pseudonym = MappingPseudonyms.Create(_hmacSecret, entityType, normalizedValue);
        var record = new MappingVaultRecord(entityType, normalizedValue, pseudonym);
        _byOriginal[key] = record;
        _byPseudonym[pseudonym] = record;
        return pseudonym;
    }

    public bool TryGetPseudonym(string entityType, string normalizedValue, out string pseudonym)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValue);

        if (_byOriginal.TryGetValue($"{entityType}\u001f{normalizedValue}", out var record))
        {
            pseudonym = record.Pseudonym;
            return true;
        }

        pseudonym = string.Empty;
        return false;
    }

    public bool TryGetOriginal(string pseudonym, out MappingVaultRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pseudonym);
        return _byPseudonym.TryGetValue(pseudonym, out record!);
    }
}
