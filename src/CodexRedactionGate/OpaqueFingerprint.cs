using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexRedactionGate;

/// <summary>
/// A validated, raw-free fingerprint used for compatibility comparisons.
/// Source values must cross the hashing boundary once before becoming this type.
/// </summary>
[JsonConverter(typeof(OpaqueFingerprintJsonConverter))]
public readonly record struct OpaqueFingerprint
{
    public string Value { get; }

    public bool IsValid => TryParse(Value, out _);

    private OpaqueFingerprint(string value)
    {
        Value = value;
    }

    public static OpaqueFingerprint FromSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new OpaqueFingerprint(Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant());
    }

    public static OpaqueFingerprint FromStored(string value)
    {
        if (!TryParse(value, out var fingerprint))
        {
            throw new ArgumentException("The stored value is not an opaque fingerprint.", nameof(value));
        }

        return fingerprint;
    }

    public static bool TryParse(string? value, out OpaqueFingerprint fingerprint)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length == 64)
        {
            try
            {
                Convert.FromHexString(value);
                fingerprint = new OpaqueFingerprint(value.ToLowerInvariant());
                return true;
            }
            catch (FormatException)
            {
                // The value has the right length but is not a hexadecimal fingerprint.
            }
        }

        fingerprint = default;
        return false;
    }

    public override string ToString() => Value;
}

internal sealed class OpaqueFingerprintJsonConverter : JsonConverter<OpaqueFingerprint>
{
    public override OpaqueFingerprint Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A persisted compatibility fingerprint must be a string.");
        }

        var value = reader.GetString();
        try
        {
            return OpaqueFingerprint.FromStored(value ?? string.Empty);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The persisted compatibility fingerprint is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        OpaqueFingerprint value,
        JsonSerializerOptions options)
    {
        if (!value.IsValid)
        {
            throw new JsonException("An invalid compatibility fingerprint cannot be persisted.");
        }

        writer.WriteStringValue(value.Value);
    }
}
