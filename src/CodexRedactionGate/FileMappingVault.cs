using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexRedactionGate;

public sealed class FileMappingVault : IMappingVault
{
    public const string DefaultVaultFileName = "vault.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _vaultFilePath;
    private readonly byte[] _hmacSecret;
    private readonly VaultStorageMode _storageMode;
    private readonly IDataProtector? _dataProtector;
    private readonly Dictionary<string, MappingVaultRecord> _byOriginal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MappingVaultRecord> _byPseudonym = new(StringComparer.Ordinal);

    private FileMappingVault(
        string vaultFilePath,
        byte[] hmacSecret,
        VaultStorageMode storageMode,
        IDataProtector? dataProtector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultFilePath);
        ArgumentNullException.ThrowIfNull(hmacSecret);

        if (hmacSecret.Length == 0)
        {
            throw new ArgumentException("HMAC secret must not be empty.", nameof(hmacSecret));
        }

        if (storageMode == VaultStorageMode.Protected && dataProtector is null)
        {
            throw new ArgumentNullException(nameof(dataProtector));
        }

        _vaultFilePath = vaultFilePath;
        _hmacSecret = (byte[])hmacSecret.Clone();
        _storageMode = storageMode;
        _dataProtector = dataProtector;

        LoadFromDisk();
    }

    public static string DefaultVaultFilePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexRedactionGate",
            DefaultVaultFileName);
    }

    public static FileMappingVault CreateProduction(byte[] hmacSecret)
    {
        return CreateProtected(DefaultVaultFilePath(), hmacSecret, new WindowsDpapiDataProtector());
    }

    public static void MigrateLegacyDefaultVaultIfNeeded(DefaultStorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var defaultRoot = Path.GetDirectoryName(DefaultVaultFilePath());
        if (string.IsNullOrWhiteSpace(defaultRoot)
            || !string.Equals(
                Path.GetFullPath(layout.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(defaultRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var legacyPath = DefaultVaultFilePath();
        var currentPath = Path.Combine(layout.VaultDirectory, DefaultVaultFileName);
        if (!File.Exists(legacyPath) || File.Exists(currentPath))
        {
            return;
        }

        Directory.CreateDirectory(layout.VaultDirectory);
        File.Copy(legacyPath, currentPath);
    }

    public static FileMappingVault CreateProtected(
        string vaultFilePath,
        byte[] hmacSecret,
        IDataProtector dataProtector)
    {
        return new FileMappingVault(vaultFilePath, hmacSecret, VaultStorageMode.Protected, dataProtector);
    }

    public static FileMappingVault CreatePlaintextForDevelopment(string vaultFilePath, byte[] hmacSecret)
    {
        return new FileMappingVault(vaultFilePath, hmacSecret, VaultStorageMode.PlaintextDevTest, dataProtector: null);
    }

    public string GetOrCreatePseudonym(string entityType, string normalizedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValue);

        using var vaultLock = VaultFileLock.Acquire(_vaultFilePath);

        LoadFromDisk();
        if (TryGetPseudonymInMemory(entityType, normalizedValue, out var existing))
        {
            return existing;
        }

        var pseudonym = MappingPseudonyms.Create(_hmacSecret, entityType, normalizedValue);
        var record = new MappingVaultRecord(entityType, normalizedValue, pseudonym);
        AddRecord(record);
        Persist();
        return pseudonym;
    }

    public bool TryGetPseudonym(string entityType, string normalizedValue, out string pseudonym)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedValue);

        using var vaultLock = VaultFileLock.Acquire(_vaultFilePath);
        LoadFromDisk();
        if (TryGetPseudonymInMemory(entityType, normalizedValue, out var record))
        {
            pseudonym = record;
            return true;
        }

        pseudonym = string.Empty;
        return false;
    }

    public bool TryGetOriginal(string pseudonym, out MappingVaultRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pseudonym);

        using var vaultLock = VaultFileLock.Acquire(_vaultFilePath);
        LoadFromDisk();
        return _byPseudonym.TryGetValue(pseudonym, out record!);
    }

    public void EnsureInitialized()
    {
        using var vaultLock = VaultFileLock.Acquire(_vaultFilePath);
        LoadFromDisk();
        if (!File.Exists(_vaultFilePath))
        {
            Persist();
        }
    }

    private bool TryGetPseudonymInMemory(string entityType, string normalizedValue, out string pseudonym)
    {
        if (_byOriginal.TryGetValue(OriginalKey(entityType, normalizedValue), out var record))
        {
            pseudonym = record.Pseudonym;
            return true;
        }

        pseudonym = string.Empty;
        return false;
    }

    private void LoadFromDisk()
    {
        _byOriginal.Clear();
        _byPseudonym.Clear();

        if (!File.Exists(_vaultFilePath))
        {
            return;
        }

        var envelope = JsonSerializer.Deserialize<VaultEnvelope>(
            File.ReadAllText(_vaultFilePath, Encoding.UTF8),
            JsonOptions) ?? throw new InvalidOperationException("Mapping vault file is empty.");

        if (envelope.Version != 1)
        {
            throw new InvalidOperationException("Unsupported mapping vault version.");
        }

        var payload = envelope.StorageMode switch
        {
            "protected" => ReadProtectedPayload(envelope),
            "plaintext_dev_test" when _storageMode == VaultStorageMode.PlaintextDevTest => envelope,
            "plaintext_dev_test" => throw new InvalidOperationException(
                "Plaintext mapping vault requires explicit development/test mode."),
            _ => throw new InvalidOperationException("Unsupported mapping vault storage mode.")
        };

        foreach (var entry in payload.Mappings)
        {
            var record = new MappingVaultRecord(entry.EntityType, entry.NormalizedValue, entry.Pseudonym);
            ValidateRecord(record);
            AddRecord(record);
        }
    }

    private VaultPayload ReadProtectedPayload(VaultEnvelope envelope)
    {
        if (_storageMode != VaultStorageMode.Protected)
        {
            throw new InvalidOperationException("Protected mapping vault requires protected storage mode.");
        }

        if (string.IsNullOrWhiteSpace(envelope.ProtectedPayload))
        {
            throw new InvalidOperationException("Protected mapping vault payload is missing.");
        }

        var protectedPayload = Convert.FromBase64String(envelope.ProtectedPayload);
        var plaintext = _dataProtector!.Unprotect(protectedPayload);
        return JsonSerializer.Deserialize<VaultPayload>(
            Encoding.UTF8.GetString(plaintext),
            JsonOptions) ?? throw new InvalidOperationException("Protected mapping vault payload is empty.");
    }

    private void Persist()
    {
        var payload = new VaultPayload
        {
            Version = 1,
            Mappings = _byOriginal.Values
                .OrderBy(record => record.EntityType, StringComparer.Ordinal)
                .ThenBy(record => record.NormalizedValue, StringComparer.Ordinal)
                .Select(record => new VaultEntry
                {
                    EntityType = record.EntityType,
                    NormalizedValue = record.NormalizedValue,
                    Pseudonym = record.Pseudonym
                })
                .ToList()
        };

        VaultEnvelope envelope;
        if (_storageMode == VaultStorageMode.Protected)
        {
            var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
            envelope = new VaultEnvelope
            {
                Version = 1,
                StorageMode = "protected",
                ProtectionKind = _dataProtector!.ProtectionKind,
                ProtectedPayload = Convert.ToBase64String(
                    _dataProtector.Protect(Encoding.UTF8.GetBytes(payloadJson)))
            };
        }
        else
        {
            envelope = new VaultEnvelope
            {
                Version = 1,
                StorageMode = "plaintext_dev_test",
                Mappings = payload.Mappings
            };
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, JsonOptions));
        AtomicFileWriter.WriteAllBytes(_vaultFilePath, bytes);
    }

    private void AddRecord(MappingVaultRecord record)
    {
        var primaryRecord = CreatePrimaryRecord(record);
        var originalKey = OriginalKey(record.EntityType, record.NormalizedValue);
        _byOriginal[originalKey] = primaryRecord;
        AddPseudonymRecord(primaryRecord);

        var legacyUsernamePseudonym = CreateLegacyUsernamePseudonym(record);
        if (legacyUsernamePseudonym is not null
            && !string.Equals(legacyUsernamePseudonym, primaryRecord.Pseudonym, StringComparison.Ordinal))
        {
            AddPseudonymRecord(primaryRecord with { Pseudonym = legacyUsernamePseudonym });
        }
    }

    private void AddPseudonymRecord(MappingVaultRecord record)
    {
        if (_byPseudonym.TryGetValue(record.Pseudonym, out var existing)
            && (existing.EntityType != record.EntityType
                || existing.NormalizedValue != record.NormalizedValue))
        {
            throw new InvalidOperationException("Mapping vault pseudonym collision detected.");
        }

        _byPseudonym[record.Pseudonym] = record;
    }

    private MappingVaultRecord CreatePrimaryRecord(MappingVaultRecord record)
    {
        if (!string.Equals(record.EntityType, SensitiveEntityTypes.Username, StringComparison.Ordinal))
        {
            return record;
        }

        var current = MappingPseudonyms.Create(_hmacSecret, record.EntityType, record.NormalizedValue);
        return record with { Pseudonym = current };
    }

    private string? CreateLegacyUsernamePseudonym(MappingVaultRecord record)
    {
        return string.Equals(record.EntityType, SensitiveEntityTypes.Username, StringComparison.Ordinal)
            ? MappingPseudonyms.CreateLegacyHex(_hmacSecret, record.EntityType, record.NormalizedValue)
            : null;
    }

    private void ValidateRecord(MappingVaultRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.EntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.NormalizedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Pseudonym);

        var expected = MappingPseudonyms.Create(_hmacSecret, record.EntityType, record.NormalizedValue);
        if (string.Equals(expected, record.Pseudonym, StringComparison.Ordinal))
        {
            return;
        }

        var legacyExpected = string.Equals(record.EntityType, SensitiveEntityTypes.Username, StringComparison.Ordinal)
            ? MappingPseudonyms.CreateLegacyHex(_hmacSecret, record.EntityType, record.NormalizedValue)
            : null;
        if (!string.Equals(legacyExpected, record.Pseudonym, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Mapping vault entry pseudonym does not match HMAC secret.");
        }
    }

    private static string OriginalKey(string entityType, string normalizedValue)
    {
        return $"{entityType}\u001f{normalizedValue}";
    }

    private enum VaultStorageMode
    {
        Protected,
        PlaintextDevTest
    }

    private sealed class VaultEnvelope : VaultPayload
    {
        [JsonPropertyName("storage_mode")]
        public string StorageMode { get; set; } = string.Empty;

        [JsonPropertyName("protection_kind")]
        public string? ProtectionKind { get; set; }

        [JsonPropertyName("protected_payload")]
        public string? ProtectedPayload { get; set; }
    }

    private class VaultPayload
    {
        public int Version { get; set; }

        public List<VaultEntry> Mappings { get; set; } = new();
    }

    private sealed class VaultEntry
    {
        [JsonPropertyName("entity_type")]
        public string EntityType { get; set; } = string.Empty;

        [JsonPropertyName("normalized_value")]
        public string NormalizedValue { get; set; } = string.Empty;

        public string Pseudonym { get; set; } = string.Empty;
    }
}
