using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexRedactionGate;

public sealed record GitleaksProvenance(
    [property: JsonPropertyName("source_repository")] string SourceRepository,
    [property: JsonPropertyName("source_revision")] string SourceRevision,
    [property: JsonPropertyName("source_tag")] string SourceTag,
    [property: JsonPropertyName("build_command")] string BuildCommand,
    [property: JsonPropertyName("go_version")] string GoVersion,
    [property: JsonPropertyName("binary_sha256")] string BinarySha256);

public static class GitleaksProvenanceLoader
{
    private static readonly Regex Sha256Regex = new(
        "^[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static GitleaksProvenance Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var provenance = JsonSerializer.Deserialize<GitleaksProvenance>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Gitleaks provenance metadata is empty.");

        Validate(provenance);
        return provenance;
    }

    private static void Validate(GitleaksProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(provenance.SourceRepository)
            || string.IsNullOrWhiteSpace(provenance.SourceRevision)
            || string.IsNullOrWhiteSpace(provenance.SourceTag)
            || string.IsNullOrWhiteSpace(provenance.BuildCommand)
            || string.IsNullOrWhiteSpace(provenance.GoVersion)
            || string.IsNullOrWhiteSpace(provenance.BinarySha256))
        {
            throw new InvalidDataException("Gitleaks provenance metadata is missing required fields.");
        }

        if (!Sha256Regex.IsMatch(provenance.BinarySha256))
        {
            throw new InvalidDataException("Gitleaks provenance metadata has an invalid binary_sha256 field.");
        }
    }
}
