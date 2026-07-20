using System;

namespace CodexRedactionGate;

internal readonly record struct SensitiveEntityTypeId
{
    private SensitiveEntityTypeId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SensitiveEntityTypeId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SensitiveEntityTypeId(value);
    }

    public override string ToString()
    {
        return Value;
    }
}

internal readonly record struct SensitiveDetectorId
{
    private SensitiveDetectorId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SensitiveDetectorId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SensitiveDetectorId(value);
    }

    public override string ToString()
    {
        return Value;
    }
}

internal readonly record struct SanitizerActionId
{
    private SanitizerActionId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static SanitizerActionId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SanitizerActionId(value);
    }

    public override string ToString()
    {
        return Value;
    }
}

internal readonly record struct ScannerStatusId
{
    private ScannerStatusId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ScannerStatusId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ScannerStatusId(value);
    }

    public bool IsFatal()
    {
        return Value is "timeout" or "invalid_json" or "scanner_error" or "configuration_error";
    }

    public override string ToString()
    {
        return Value;
    }
}

internal static class SensitiveEntityTypeIds
{
    public static SensitiveEntityTypeId FromPublic(string value) => SensitiveEntityTypeId.From(value);
}

internal static class SensitiveDetectorIds
{
    public static readonly SensitiveDetectorId SyntheticBlock = SensitiveDetectorId.From(SanitizerPipelineConstants.BlockDetectorId);
    public static readonly SensitiveDetectorId SyntheticMarker = SensitiveDetectorId.From(SanitizerPipelineConstants.SyntheticDetectorId);
    public static readonly SensitiveDetectorId Dictionary = SensitiveDetectorId.From(SanitizerPipelineConstants.DictionaryDetectorId);
    public static readonly SensitiveDetectorId Technical = SensitiveDetectorId.From(SanitizerPipelineConstants.TechnicalDetectorId);
    public static readonly SensitiveDetectorId SecretRegex = SensitiveDetectorId.From("secret_regex");
    public static readonly SensitiveDetectorId Gitleaks = SensitiveDetectorId.From("gitleaks");

    public static SensitiveDetectorId FromPublic(string value) => SensitiveDetectorId.From(value);
}

internal static class SanitizerActionIds
{
    public static readonly SanitizerActionId BlockSynthetic = SanitizerActionId.From(SanitizerPipelineConstants.BlockAction);
    public static readonly SanitizerActionId ReplaceSynthetic = SanitizerActionId.From(SanitizerPipelineConstants.SyntheticAction);
    public static readonly SanitizerActionId PseudonymizeRestorable = SanitizerActionId.From(PolicyActions.PseudonymizeRestorable);
    public static readonly SanitizerActionId RedactNonRestorable = SanitizerActionId.From(PolicyActions.RedactNonRestorable);

    public static SanitizerActionId FromPublic(string value) => SanitizerActionId.From(value);
}

internal static class ScannerStatusIds
{
    public static readonly ScannerStatusId Timeout = ScannerStatusId.From("timeout");
    public static readonly ScannerStatusId InvalidJson = ScannerStatusId.From("invalid_json");
    public static readonly ScannerStatusId ScannerError = ScannerStatusId.From("scanner_error");
    public static readonly ScannerStatusId ConfigurationError = ScannerStatusId.From("configuration_error");
    public static readonly ScannerStatusId NoFindings = ScannerStatusId.From("no_findings");
    public static readonly ScannerStatusId Findings = ScannerStatusId.From("findings");
    public static readonly ScannerStatusId Ok = ScannerStatusId.From("ok");

    public static ScannerStatusId FromPublic(string value) => ScannerStatusId.From(value);
}
