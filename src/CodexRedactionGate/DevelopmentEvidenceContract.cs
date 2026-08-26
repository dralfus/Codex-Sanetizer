using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexRedactionGate;

public static class DevelopmentEvidenceContract
{
    public const string SchemaVersion = "1";
}

public static class DevelopmentEvidenceRecordLocation
{
    public const string CurrentTicketNumber = "351";
    public static string VerificationArtifactRelativePath => Path.Combine(
        "artifacts",
        "evidence",
        "351-proof.txt");

    public static string RelativePath => ForTicket(CurrentTicketNumber);

    public static string ForTicket(string ticketNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketNumber);
        if (!ticketNumber.All(character => character is >= '0' and <= '9'))
        {
            throw new ArgumentException("ticket_number_invalid", nameof(ticketNumber));
        }

        return Path.Combine("artifacts", "evidence", ticketNumber + ".json");
    }

    public static string Resolve(string repositoryRoot)
        => Resolve(repositoryRoot, CurrentTicketNumber);

    public static string ResolveVerificationArtifact(string repositoryRoot)
        => ResolveRelative(repositoryRoot, VerificationArtifactRelativePath);

    public static string Resolve(string repositoryRoot, string ticketNumber)
        => ResolveRelative(repositoryRoot, ForTicket(ticketNumber));

    private static string ResolveRelative(string repositoryRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("evidence_repository_missing");
        }

        RejectReparsePoint(root);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("evidence_record_path_outside_repository");
        }

        var current = root;
        foreach (var segment in relativePath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current))
            {
                RejectReparsePoint(current);
            }
        }

        return path;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("evidence_path_reparse_point");
        }
    }
}

public enum DevelopmentEvidenceState
{
    Proposed,
    ReproducedRed,
    Implemented,
    LocallyVerified,
    LiveVerified,
    Released
}

public static class DevelopmentEvidenceStateTokens
{
    private static readonly IReadOnlyDictionary<DevelopmentEvidenceState, string> Tokens =
        new Dictionary<DevelopmentEvidenceState, string>
        {
            [DevelopmentEvidenceState.Proposed] = "proposed",
            [DevelopmentEvidenceState.ReproducedRed] = "reproduced_red",
            [DevelopmentEvidenceState.Implemented] = "implemented",
            [DevelopmentEvidenceState.LocallyVerified] = "locally_verified",
            [DevelopmentEvidenceState.LiveVerified] = "live_verified",
            [DevelopmentEvidenceState.Released] = "released"
        };

    public static string ToToken(DevelopmentEvidenceState state)
        => Tokens.TryGetValue(state, out var token)
            ? token
            : throw new ArgumentOutOfRangeException(nameof(state));

    public static bool TryParse(string? token, out DevelopmentEvidenceState state)
    {
        foreach (var pair in Tokens)
        {
            if (string.Equals(pair.Value, token, StringComparison.Ordinal))
            {
                state = pair.Key;
                return true;
            }
        }

        state = default;
        return false;
    }

    public static bool TryAdvance(
        DevelopmentEvidenceState current,
        DevelopmentEvidenceState next)
    {
        return Enum.IsDefined(current)
            && Enum.IsDefined(next)
            && (int)next == (int)current + 1;
    }
}

public sealed record ProtectedSendEvidenceRecord
{
    public ProtectedSendEvidenceRecord(
        string SchemaVersion,
        string TicketId,
        string BehaviorId,
        DevelopmentEvidenceState State,
        string ReproductionId,
        string ReproductionCommandId,
        string HighestRequiredSeam,
        string BuildVersion,
        string SourceCommit,
        string ExecutableSha256,
        string InstallerIdentity,
        string CompatibilityFingerprint,
        string SubmitBinding,
        IReadOnlyList<DevelopmentEvidenceState> TransitionHistory,
        string Claim = "unverified",
        string ValidatorArtifactSha256 = "unbound",
        string VerificationArtifactSha256 = "unbound")
    {
        this.SchemaVersion = SchemaVersion;
        this.TicketId = TicketId;
        this.BehaviorId = BehaviorId;
        this.State = State;
        this.ReproductionId = ReproductionId;
        this.ReproductionCommandId = ReproductionCommandId;
        this.HighestRequiredSeam = HighestRequiredSeam;
        this.BuildVersion = BuildVersion;
        this.SourceCommit = SourceCommit;
        this.ExecutableSha256 = ExecutableSha256;
        this.InstallerIdentity = InstallerIdentity;
        this.CompatibilityFingerprint = CompatibilityFingerprint;
        this.SubmitBinding = SubmitBinding;
        this.TransitionHistory = Array.AsReadOnly(TransitionHistory?.ToArray()
            ?? throw new ArgumentNullException(nameof(TransitionHistory)));
        this.Claim = Claim;
        this.ValidatorArtifactSha256 = ValidatorArtifactSha256;
        this.VerificationArtifactSha256 = VerificationArtifactSha256;
    }

    public string SchemaVersion { get; init; }
    public string TicketId { get; init; }
    public string BehaviorId { get; init; }
    public DevelopmentEvidenceState State { get; init; }
    public string ReproductionId { get; init; }
    public string ReproductionCommandId { get; init; }
    public string HighestRequiredSeam { get; init; }
    public string BuildVersion { get; init; }
    public string SourceCommit { get; init; }
    public string ExecutableSha256 { get; init; }
    public string InstallerIdentity { get; init; }
    public string CompatibilityFingerprint { get; init; }
    public string SubmitBinding { get; init; }
    public IReadOnlyList<DevelopmentEvidenceState> TransitionHistory { get; private init; }
    public string Claim { get; init; }
    public string ValidatorArtifactSha256 { get; init; }
    public string VerificationArtifactSha256 { get; init; }
}

public sealed record ProtectedSendEvidenceBinding(
    string SchemaVersion,
    string BuildVersion,
    string SourceCommit,
    string ExecutableSha256,
    string InstallerIdentity,
    string CompatibilityFingerprint,
    string SubmitBinding,
    string ValidatorArtifactSha256 = "unbound",
    string VerificationArtifactSha256 = "unbound");

public sealed record EvidenceValidationResult(bool Valid, string Code);

public static class ProtectedSendEvidenceValidator
{
    private const string Unbound = "unbound";
    private const string FixedClaim = "fixed";
    private const string ReleasedClaim = "released";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static EvidenceValidationResult Validate(
        ProtectedSendEvidenceRecord record,
        string expectedBuildVersion,
        string expectedSourceCommit,
        DevelopmentEvidenceState minimumState)
        => Validate(
            record,
            new ProtectedSendEvidenceBinding(
                DevelopmentEvidenceContract.SchemaVersion,
                expectedBuildVersion,
                expectedSourceCommit,
                Unbound,
                Unbound,
                Unbound,
                Unbound,
                Unbound),
            minimumState);

    public static EvidenceValidationResult Validate(
        ProtectedSendEvidenceRecord record,
        ProtectedSendEvidenceBinding expected,
        DevelopmentEvidenceState minimumState)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(expected);

        if (!Enum.IsDefined(record.State))
        {
            return Invalid("invalid_evidence_state");
        }

        if (!Enum.IsDefined(minimumState))
        {
            return Invalid("invalid_required_evidence_state");
        }

        if (!IsSafeIdentifier(expected.SchemaVersion)
            || expected.SchemaVersion != DevelopmentEvidenceContract.SchemaVersion)
        {
            return Invalid("unsupported_schema_version");
        }

        if (!IsBoundBuildVersion(expected.BuildVersion))
        {
            return Invalid("invalid_expected_build_version");
        }

        if (!IsSafeCommit(expected.SourceCommit)
            || !IsSafeHash(expected.ExecutableSha256)
            || !IsSafeBuildVersion(expected.InstallerIdentity)
            || !IsSafeHash(expected.CompatibilityFingerprint)
            || !IsSafeIdentifier(expected.SubmitBinding)
            || !IsSafeHash(expected.ValidatorArtifactSha256)
            || !IsSafeHash(expected.VerificationArtifactSha256))
        {
            return Invalid("invalid_expected_binding");
        }

        if (!string.Equals(record.SchemaVersion, expected.SchemaVersion, StringComparison.Ordinal)
            || !IsSafeIdentifier(record.SchemaVersion))
        {
            return Invalid("invalid_schema_version");
        }

        if (!IsSafeIdentifier(record.TicketId))
        {
            return Invalid("invalid_ticket_id");
        }

        if (!IsSafeIdentifier(record.BehaviorId))
        {
            return Invalid("invalid_behavior_id");
        }

        if (!IsSafeIdentifier(record.ReproductionId))
        {
            return Invalid("invalid_reproduction_id");
        }

        if (!IsSafeIdentifier(record.ReproductionCommandId))
        {
            return Invalid("invalid_reproduction_command");
        }

        if (!IsSafeIdentifier(record.HighestRequiredSeam))
        {
            return Invalid("invalid_highest_required_seam");
        }

        if (!IsBoundBuildVersion(record.BuildVersion))
        {
            return Invalid("invalid_build_version");
        }

        if (!IsSafeCommit(record.SourceCommit))
        {
            return Invalid("invalid_source_commit");
        }

        if (!IsSafeHash(record.ExecutableSha256))
        {
            return Invalid("invalid_executable_hash");
        }

        if (!IsSafeBuildVersion(record.InstallerIdentity))
        {
            return Invalid("invalid_installer_identity");
        }

        if (!IsSafeHash(record.CompatibilityFingerprint))
        {
            return Invalid("invalid_compatibility_fingerprint");
        }

        if (!IsSafeIdentifier(record.SubmitBinding))
        {
            return Invalid("invalid_submit_binding");
        }

        if (!IsSafeHash(record.ValidatorArtifactSha256))
        {
            return Invalid("invalid_validator_artifact_hash");
        }

        if (!IsSafeHash(record.VerificationArtifactSha256))
        {
            return Invalid("invalid_verification_artifact_hash");
        }

        if (!IsSafeClaim(record.Claim))
        {
            return Invalid("invalid_evidence_claim");
        }

        if ((int)record.State < (int)minimumState)
        {
            return Invalid("evidence_state_below_required");
        }

        if (!string.Equals(record.BuildVersion, expected.BuildVersion, StringComparison.Ordinal))
        {
            return Invalid("evidence_build_mismatch");
        }

        if (!string.Equals(record.SourceCommit, expected.SourceCommit, StringComparison.Ordinal)
            && expected.SourceCommit != Unbound)
        {
            return Invalid("evidence_source_mismatch");
        }

        if (!HasValidHistory(record))
        {
            return Invalid("invalid_evidence_history");
        }

        if ((int)record.State >= (int)DevelopmentEvidenceState.LiveVerified)
        {
            if (record.SourceCommit == Unbound
                || record.ExecutableSha256 == Unbound
                || record.InstallerIdentity == Unbound
                || record.CompatibilityFingerprint == Unbound
                || record.SubmitBinding == Unbound
                || record.ValidatorArtifactSha256 == Unbound
                || record.VerificationArtifactSha256 == Unbound
                || expected.SourceCommit == Unbound
                || expected.ExecutableSha256 == Unbound
                || expected.InstallerIdentity == Unbound
                || expected.CompatibilityFingerprint == Unbound
                || expected.SubmitBinding == Unbound
                || expected.ValidatorArtifactSha256 == Unbound
                || expected.VerificationArtifactSha256 == Unbound)
            {
                return Invalid("expected_binding_incomplete");
            }
        }

        var artifactMismatch = CompareExpectedArtifacts(record, expected);
        if (artifactMismatch is not null)
        {
            return Invalid(artifactMismatch);
        }

        if (record.Claim == FixedClaim
            && (int)record.State < (int)DevelopmentEvidenceState.LiveVerified)
        {
            return Invalid("fixed_claim_requires_live_verified");
        }

        if (record.Claim == ReleasedClaim
            && record.State != DevelopmentEvidenceState.Released)
        {
            return Invalid("released_claim_requires_released_state");
        }

        return new EvidenceValidationResult(true, "valid");
    }

    public static string Serialize(ProtectedSendEvidenceRecord record)
        => SerializeValidated(record, expected: null);

    public static string Serialize(
        ProtectedSendEvidenceRecord record,
        ProtectedSendEvidenceBinding expected)
        => SerializeValidated(record, expected);

    private static string SerializeValidated(
        ProtectedSendEvidenceRecord record,
        ProtectedSendEvidenceBinding? expected)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.State >= DevelopmentEvidenceState.LiveVerified
            && expected is null)
        {
            throw new InvalidOperationException("external_binding_required");
        }

        expected ??= new ProtectedSendEvidenceBinding(
            record.SchemaVersion,
            record.BuildVersion,
            record.SourceCommit,
            Unbound,
            Unbound,
            Unbound,
            Unbound);
        var validation = Validate(
            record,
            expected,
            record.State);
        if (!validation.Valid)
        {
            throw new InvalidOperationException(validation.Code);
        }

        return JsonSerializer.Serialize(record, JsonOptions);
    }

    public static bool TryDeserialize(
        string json,
        out ProtectedSendEvidenceRecord? record,
        out EvidenceValidationResult result)
    {
        record = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            result = Invalid("invalid_evidence_record_json");
            return false;
        }

        try
        {
            record = JsonSerializer.Deserialize<ProtectedSendEvidenceRecord>(json, JsonOptions);
            if (record is null)
            {
                result = Invalid("invalid_evidence_record_json");
                return false;
            }

            result = new EvidenceValidationResult(true, "deserialized");
            return true;
        }
        catch (JsonException)
        {
            result = Invalid("invalid_evidence_record_json");
            return false;
        }
        catch (NotSupportedException)
        {
            result = Invalid("invalid_evidence_record_json");
            return false;
        }
    }

    internal static bool IsCompleteBinding(ProtectedSendEvidenceBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return IsSafeCommit(binding.SourceCommit)
            && binding.SourceCommit != Unbound
            && IsSafeHash(binding.ExecutableSha256)
            && binding.ExecutableSha256 != Unbound
            && IsSafeBuildVersion(binding.InstallerIdentity)
            && binding.InstallerIdentity != Unbound
            && IsSafeHash(binding.CompatibilityFingerprint)
            && binding.CompatibilityFingerprint != Unbound
            && IsSafeIdentifier(binding.SubmitBinding)
            && binding.SubmitBinding != Unbound
            && IsSafeHash(binding.ValidatorArtifactSha256)
            && binding.ValidatorArtifactSha256 != Unbound
            && IsSafeHash(binding.VerificationArtifactSha256)
            && binding.VerificationArtifactSha256 != Unbound;
    }

    internal static bool IsCompleteRecord(ProtectedSendEvidenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return IsSafeCommit(record.SourceCommit)
            && record.SourceCommit != Unbound
            && IsSafeHash(record.ExecutableSha256)
            && record.ExecutableSha256 != Unbound
            && IsSafeBuildVersion(record.InstallerIdentity)
            && record.InstallerIdentity != Unbound
            && IsSafeHash(record.CompatibilityFingerprint)
            && record.CompatibilityFingerprint != Unbound
            && IsSafeIdentifier(record.SubmitBinding)
            && record.SubmitBinding != Unbound
            && IsSafeHash(record.ValidatorArtifactSha256)
            && record.ValidatorArtifactSha256 != Unbound
            && IsSafeHash(record.VerificationArtifactSha256)
            && record.VerificationArtifactSha256 != Unbound;
    }

    private static EvidenceValidationResult Invalid(string code)
        => new(false, code);

    private static bool IsSafeIdentifier(string? value)
        => IsSafeToken(value, maxLength: 96);

    private static bool IsSafeBuildVersion(string? value)
    {
        if (string.Equals(value, Unbound, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '.'
            or '-'
            or '_'
            or '+');
    }

    private static bool IsBoundBuildVersion(string? value)
        => IsSafeBuildVersion(value) && !string.Equals(value, Unbound, StringComparison.Ordinal);

    private static bool HasValidHistory(ProtectedSendEvidenceRecord record)
    {
        if (record.TransitionHistory is null
            || record.TransitionHistory.Count != (int)record.State + 1
            || record.TransitionHistory[0] != DevelopmentEvidenceState.Proposed
            || record.TransitionHistory[^1] != record.State)
        {
            return false;
        }

        for (var index = 1; index < record.TransitionHistory.Count; index++)
        {
            if (!DevelopmentEvidenceStateTokens.TryAdvance(
                    record.TransitionHistory[index - 1],
                    record.TransitionHistory[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string? CompareExpectedArtifacts(
        ProtectedSendEvidenceRecord record,
        ProtectedSendEvidenceBinding expected)
    {
        if (expected.SchemaVersion != DevelopmentEvidenceContract.SchemaVersion)
        {
            return "unsupported_schema_version";
        }

        if (expected.ExecutableSha256 != Unbound
            && record.ExecutableSha256 != expected.ExecutableSha256)
        {
            return "evidence_executable_mismatch";
        }

        if (expected.InstallerIdentity != Unbound
            && record.InstallerIdentity != expected.InstallerIdentity)
        {
            return "evidence_installer_mismatch";
        }

        if (expected.CompatibilityFingerprint != Unbound
            && record.CompatibilityFingerprint != expected.CompatibilityFingerprint)
        {
            return "evidence_compatibility_mismatch";
        }

        if (expected.SubmitBinding != Unbound
            && record.SubmitBinding != expected.SubmitBinding)
        {
            return "evidence_binding_mismatch";
        }

        if (expected.ValidatorArtifactSha256 != Unbound
            && record.ValidatorArtifactSha256 != expected.ValidatorArtifactSha256)
        {
            return "evidence_validator_artifact_mismatch";
        }

        if (expected.VerificationArtifactSha256 != Unbound
            && record.VerificationArtifactSha256 != expected.VerificationArtifactSha256)
        {
            return "evidence_verification_artifact_mismatch";
        }

        return null;
    }

    private static bool IsSafeCommit(string? value)
        => string.Equals(value, Unbound, StringComparison.Ordinal)
            || IsHex(value, 40, 64);

    private static bool IsSafeHash(string? value)
        => string.Equals(value, Unbound, StringComparison.Ordinal)
            || IsHex(value, 64, 64);

    private static bool IsHex(string? value, int minimumLength, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < minimumLength
            || value.Length > maximumLength)
        {
            return false;
        }

        return value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');
    }

    private static bool IsSafeClaim(string? value)
        => value is "unverified" or FixedClaim or ReleasedClaim;

    private static bool IsSafeToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_');
    }
}

public static class ProtectedSendEvidenceRecordGate
{
    public static EvidenceValidationResult ValidateCurrent(
        string repositoryRoot,
        ProtectedSendEvidenceBinding expected,
        DevelopmentEvidenceState minimumState)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (!ProtectedSendEvidenceValidator.IsCompleteBinding(expected))
        {
            return new EvidenceValidationResult(false, "current_evidence_expected_binding_incomplete");
        }

        string path;
        try
        {
            path = DevelopmentEvidenceRecordLocation.Resolve(repositoryRoot);
        }
        catch (ArgumentException)
        {
            return new EvidenceValidationResult(false, "current_evidence_repository_invalid");
        }
        catch (InvalidOperationException)
        {
            return new EvidenceValidationResult(false, "current_evidence_repository_invalid");
        }
        catch (IOException)
        {
            return new EvidenceValidationResult(false, "current_evidence_repository_invalid");
        }
        catch (UnauthorizedAccessException)
        {
            return new EvidenceValidationResult(false, "current_evidence_repository_invalid");
        }

        if (!File.Exists(path))
        {
            return new EvidenceValidationResult(false, "current_evidence_record_missing");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return new EvidenceValidationResult(false, "current_evidence_record_unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new EvidenceValidationResult(false, "current_evidence_record_unreadable");
        }

        if (!ProtectedSendEvidenceValidator.TryDeserialize(json, out var record, out _)
            || record is null)
        {
            return new EvidenceValidationResult(false, "current_evidence_record_invalid");
        }

        if ((int)record.State < (int)minimumState)
        {
            return new EvidenceValidationResult(false, "current_evidence_state_below_required");
        }

        if (!string.Equals(record.TicketId, "ticket_351", StringComparison.Ordinal)
            || !string.Equals(record.BehaviorId, "protected_send_evidence", StringComparison.Ordinal)
            || !string.Equals(record.ReproductionId, "repro_protected_send", StringComparison.Ordinal)
            || !string.Equals(record.ReproductionCommandId, "cmd_repro_protected_send", StringComparison.Ordinal)
            || !string.Equals(record.HighestRequiredSeam, "deterministic_transaction", StringComparison.Ordinal))
        {
            return new EvidenceValidationResult(false, "current_evidence_contract_mismatch");
        }

        if (!ProtectedSendEvidenceValidator.IsCompleteRecord(record))
        {
            return new EvidenceValidationResult(false, "current_evidence_binding_incomplete");
        }

        string verificationPath;
        try
        {
            verificationPath = DevelopmentEvidenceRecordLocation.ResolveVerificationArtifact(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            return new EvidenceValidationResult(false, "current_evidence_verification_artifact_invalid");
        }

        if (!File.Exists(verificationPath))
        {
            return new EvidenceValidationResult(false, "current_evidence_verification_artifact_missing");
        }

        string verificationHash;
        try
        {
            verificationHash = Convert.ToHexString(
                    SHA256.HashData(File.ReadAllBytes(verificationPath)))
                .ToLowerInvariant();
        }
        catch (IOException)
        {
            return new EvidenceValidationResult(false, "current_evidence_verification_artifact_unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new EvidenceValidationResult(false, "current_evidence_verification_artifact_unreadable");
        }

        if (!string.Equals(
                verificationHash,
                expected.VerificationArtifactSha256,
                StringComparison.Ordinal))
        {
            return new EvidenceValidationResult(false, "evidence_verification_artifact_mismatch");
        }

        return ProtectedSendEvidenceValidator.Validate(record, expected, minimumState);
    }
}

public static class DevelopmentEvidenceContractSmoke
{
    public static bool Run()
    {
        var record = new ProtectedSendEvidenceRecord(
            SchemaVersion: DevelopmentEvidenceContract.SchemaVersion,
            TicketId: "ticket_351",
            BehaviorId: "protected_send_evidence",
            State: DevelopmentEvidenceState.Implemented,
            ReproductionId: "repro_protected_send",
            ReproductionCommandId: "cmd_repro_protected_send",
            HighestRequiredSeam: "deterministic_transaction",
            BuildVersion: BuildVersion.Current,
            SourceCommit: "unbound",
            ExecutableSha256: "unbound",
            InstallerIdentity: "unbound",
            CompatibilityFingerprint: "unbound",
            SubmitBinding: "unbound",
            TransitionHistory: EvidenceHistory(DevelopmentEvidenceState.Implemented),
            ValidatorArtifactSha256: "unbound");
        var valid = ProtectedSendEvidenceValidator.Validate(
            record,
            BuildVersion.Current,
            "unbound",
            DevelopmentEvidenceState.Implemented);
        var prematureFixed = ProtectedSendEvidenceValidator.Validate(
            record with { Claim = "fixed" },
            BuildVersion.Current,
            "unbound",
            DevelopmentEvidenceState.Implemented);

        return valid.Valid
            && !prematureFixed.Valid
            && prematureFixed.Code == "fixed_claim_requires_live_verified"
            && ProtectedSendEvidenceValidator.Serialize(record).Contains(
                "\"state\":\"implemented\"",
                StringComparison.Ordinal);
    }

    private static DevelopmentEvidenceState[] EvidenceHistory(DevelopmentEvidenceState state)
    {
        var history = new DevelopmentEvidenceState[(int)state + 1];
        for (var index = 0; index < history.Length; index++)
        {
            history[index] = (DevelopmentEvidenceState)index;
        }

        return history;
    }
}
