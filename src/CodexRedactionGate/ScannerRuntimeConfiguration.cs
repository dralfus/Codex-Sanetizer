using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexRedactionGate;

public sealed record ScannerRuntimeConfigurationReport(
    bool Valid,
    bool BinaryPresent,
    bool ProvenanceLoaded,
    bool BinaryChecksumMatches,
    string? WarningCode,
    bool SafeDisabled,
    bool RequiresGit,
    bool RequiresGo,
    bool RequiresGitleaksSourceCode,
    bool RequiresNetwork)
{
    public static ScannerRuntimeConfigurationReport ValidLocalArtifact { get; } = new(
        Valid: true,
        BinaryPresent: true,
        ProvenanceLoaded: true,
        BinaryChecksumMatches: true,
        WarningCode: null,
        SafeDisabled: false,
        RequiresGit: false,
        RequiresGo: false,
        RequiresGitleaksSourceCode: false,
        RequiresNetwork: false);

    public static ScannerRuntimeConfigurationReport SafeDisabledLocalPackageMissing { get; } = new(
        Valid: false,
        BinaryPresent: false,
        ProvenanceLoaded: false,
        BinaryChecksumMatches: false,
        WarningCode: "scanner_package_missing_safe_disabled",
        SafeDisabled: true,
        RequiresGit: false,
        RequiresGo: false,
        RequiresGitleaksSourceCode: false,
        RequiresNetwork: false);
}

public static class ScannerRuntimeConfigurationValidator
{
    public static ScannerRuntimeConfigurationReport Validate(MvpPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var binaryPresent = File.Exists(manifest.GitleaksBinaryPath);
        var provenance = TryLoadProvenance(manifest.GitleaksProvenancePath);
        var provenanceLoaded = provenance is not null;
        var checksumMatches = binaryPresent && provenanceLoaded && BinaryChecksumMatches(manifest.GitleaksBinaryPath, provenance!);
        var warningCode = GetWarningCode(binaryPresent, provenanceLoaded, checksumMatches);

        return new ScannerRuntimeConfigurationReport(
            Valid: binaryPresent && provenanceLoaded && checksumMatches,
            BinaryPresent: binaryPresent,
            ProvenanceLoaded: provenanceLoaded,
            BinaryChecksumMatches: checksumMatches,
            WarningCode: warningCode,
            SafeDisabled: false,
            RequiresGit: false,
            RequiresGo: false,
            RequiresGitleaksSourceCode: false,
            RequiresNetwork: false);
    }

    private static string? GetWarningCode(bool binaryPresent, bool provenanceLoaded, bool checksumMatches)
    {
        if (!binaryPresent)
        {
            return "scanner_binary_missing";
        }

        if (!provenanceLoaded)
        {
            return "scanner_provenance_invalid";
        }

        if (!checksumMatches)
        {
            return "scanner_checksum_mismatch";
        }

        return null;
    }

    private static GitleaksProvenance? TryLoadProvenance(string path)
    {
        try
        {
            return GitleaksProvenanceLoader.Load(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool BinaryChecksumMatches(string binaryPath, GitleaksProvenance provenance)
    {
        var hash = SHA256.HashData(File.ReadAllBytes(binaryPath));
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, provenance.BinarySha256, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record DefaultScannerPackageResolution(
    MvpPackageManifest Manifest,
    ScannerRuntimeConfigurationReport Report,
    bool AnyScannerArtifactPresent);

public static class ScannerPackageManifestResolver
{
    public const string ScannerDirectoryName = "scanners";
    public const string GitleaksDirectoryName = "gitleaks";
    public const string GitleaksBinaryFileName = "gitleaks.exe";
    public const string GitleaksProvenanceFileName = "gitleaks-provenance.json";

    public static MvpPackageManifest CreateDefault(string appBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        var baseDirectory = Path.GetFullPath(appBaseDirectory);
        var scannerDirectory = Path.Combine(baseDirectory, ScannerDirectoryName, GitleaksDirectoryName);
        return new MvpPackageManifest(
            AppArtifactPath: Path.Combine(baseDirectory, "CodexRedactionGate.dll"),
            GitleaksBinaryPath: Path.Combine(scannerDirectory, GitleaksBinaryFileName),
            GitleaksProvenancePath: Path.Combine(scannerDirectory, GitleaksProvenanceFileName));
    }

    public static DefaultScannerPackageResolution ResolveDefault(string appBaseDirectory)
    {
        var manifest = CreateDefault(appBaseDirectory);
        var anyScannerArtifactPresent = File.Exists(manifest.GitleaksBinaryPath)
            || File.Exists(manifest.GitleaksProvenancePath);
        var report = anyScannerArtifactPresent
            ? ScannerRuntimeConfigurationValidator.Validate(manifest)
            : ScannerRuntimeConfigurationReport.SafeDisabledLocalPackageMissing;

        return new DefaultScannerPackageResolution(
            Manifest: manifest,
            Report: report,
            AnyScannerArtifactPresent: anyScannerArtifactPresent);
    }
}

public sealed class ScannerConfigurationGuardedSecretScanner : ISecretScanner
{
    private readonly ISecretScanner _inner;
    private readonly Func<ScannerRuntimeConfigurationReport> _validate;

    public ScannerConfigurationGuardedSecretScanner(
        ISecretScanner inner,
        Func<ScannerRuntimeConfigurationReport> validate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(validate);

        _inner = inner;
        _validate = validate;
    }

    public SecretScanResult Scan(string input, TimeSpan timeout)
    {
        var report = _validate();
        if (!report.Valid)
        {
            return new SecretScanResult(
                TimedOut: false,
                ScannerStatus: ScannerStatusIds.ConfigurationError.Value,
                Findings: Array.Empty<GitleaksFindingSpan>());
        }

        return _inner.Scan(input, timeout);
    }
}
